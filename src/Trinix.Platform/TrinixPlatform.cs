using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Trinix.Interop;
using Vixen.Core.Mathematics;
using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     Trinix, as Vixen's <see cref="IPlatform" />.
/// </summary>
/// <remarks>
///     <para>
///         The fourth desktop implementation, and the one that is not SDL.
///         <c>Vixen.Platform.Desktop</c> covers Windows, Linux and macOS through one
///         library; this covers Trinix through the protocol Trinix's own compositor
///         speaks, including the two extensions SDL has never heard of — decorations
///         and the global menu bar, which are most of what makes a window on Trinix
///         look like a window on Trinix.
///     </para>
///     <para>
///         <b>Where the code lives is a rule rather than a preference.</b> This is
///         Trinix-specific and therefore in Trinix, not in Vixen. What Vixen needed was
///         one generic hook — <c>UiApplicationOptions.Platform</c> — so that the loop
///         which opens a window can be told to use something other than SDL.
///     </para>
///     <para>
///         <b>Threading.</b> Owned by the thread that constructed it, like every
///         <see cref="IPlatform" />. Wayland enforces the same rule from the other
///         side: a <c>wl_display</c> dispatched from two threads without queues is a
///         protocol error, not a race that sometimes works.
///     </para>
/// </remarks>
public sealed class TrinixPlatform : IPlatform {
    /// <summary>
    ///     The running platform, for the unmanaged callbacks to find.
    /// </summary>
    /// <remarks>
    ///     ⚠ Static because a <c>delegate* unmanaged</c> cannot close over anything —
    ///     the same arrangement the compositor uses for wlroots' callbacks. One
    ///     platform per process is not a simplification: it is what
    ///     <see cref="IPlatform" />'s own threading rule already implies, and a second
    ///     one would be a second connection dispatching the same display.
    /// </remarks>
    static TrinixPlatform? s_current;

    readonly Dictionary<IntPtr, TrinixWindow> windowsByHandle = [];
    readonly Dictionary<uint, TrinixWindow> windowsById = [];
    readonly List<IWindow> windows = [];
    readonly PlatformEventBuffer events = new();

    readonly TrinixDisplays displays = new();
    readonly TrinixInput input = new();
    readonly TrinixLifecycle lifecycle = new();
    readonly TrinixTextInput textInput = new();

    readonly IntPtr client;
    readonly long started = Stopwatch.GetTimestamp();

    uint nextWindowId = 1;
    bool disposed;

    /// <summary>Connects to the compositor named by <c>WAYLAND_DISPLAY</c>.</summary>
    /// <param name="options">Who the application is, which decides where its files live.</param>
    /// <exception cref="PlatformNotSupportedException">
    ///     There is no compositor to talk to. Deliberately an exception here and not at
    ///     the call sites: an application head that asked for the Trinix platform has
    ///     already decided it wants a window, and a platform that connected to nothing
    ///     and reported no windowing capability would fail later and less clearly.
    /// </exception>
    public TrinixPlatform(TrinixPlatformOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        FileSystem = TrinixServices.FileSystem(options.Organisation, options.Application);
        ApplicationId = options.ApplicationId;

        var callbacks = BuildCallbacks();
        client = WaylandClient.Connect(callbacks, IntPtr.Zero);

        if (client == IntPtr.Zero) {
            throw new PlatformNotSupportedException(
                "No Wayland compositor answered. WAYLAND_DISPLAY names the socket under XDG_RUNTIME_DIR; "
                + "trinix-compositor.service is what provides it."
            );
        }

        s_current = this;
        DisplayHandle = WaylandClient.Display(client);
        ClientHandle = client;

        var globals = WaylandClient.AvailableGlobals(client);
        Capabilities = PlatformCapabilities.Windowing
            | PlatformCapabilities.MultiWindow
            | PlatformCapabilities.DisplayEnumeration
            | PlatformCapabilities.TextInput;

        Name = globals.HasFlag(WaylandClient.Globals.Shell) ? "Trinix" : "Trinix (undecorated)";
    }

    /// <summary>The <c>wl_display</c> every surface on this platform belongs to.</summary>
    /// <remarks>
    ///     Static for <see cref="TrinixSurface" />'s benefit, which is constructed per
    ///     window and needs the one display without holding the platform.
    /// </remarks>
    internal static IntPtr DisplayHandle { get; private set; }

    /// <summary>The connection this process's menus belong to, or zero.</summary>
    /// <remarks>
    ///     Static for the same reason as <see cref="DisplayHandle" />, and needed for
    ///     one thing in particular: a menu bar is scoped to the connection rather than
    ///     to a window, so <see cref="TrinixMenu.ForApplication" /> has to name the
    ///     connection and has no window to reach it through — which is the point, since
    ///     an application may have none.
    /// </remarks>
    internal static IntPtr ClientHandle { get; private set; }

    /// <summary>The identifier the shell groups this application's windows by.</summary>
    internal string ApplicationId { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public PlatformCapabilities Capabilities { get; }

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => windows;

    /// <inheritdoc />
    public IDisplayInfo Displays => displays;

    /// <inheritdoc />
    public IFileSystemHost FileSystem { get; }

    /// <inheritdoc />
    public IClipboard Clipboard { get; } = new TrinixClipboard();

    /// <inheritdoc />
    public INativeDialogs Dialogs { get; } = new TrinixDialogs();

    /// <inheritdoc />
    public ILifecycle Lifecycle => lifecycle;

    /// <inheritdoc />
    public IInputSource Input => input;

    /// <inheritdoc />
    public ITextInput TextInput => textInput;

    /// <inheritdoc />
    public IPowerInfo Power { get; } = new TrinixPowerInfo();

    /// <inheritdoc />
    public IProcessorTopology Processors { get; } = new TrinixProcessors();

    /// <inheritdoc />
    public IWindow CreateWindow(in WindowOptions options) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var id = nextWindowId++;
        var handle = WaylandClient.CreateWindow(
            client,
            options.Title,
            ApplicationId,
            options.Size.X,
            options.Size.Y,
            options.IsResizable
        );

        if (handle == IntPtr.Zero) {
            throw new PlatformNotSupportedException("The compositor refused a window.");
        }

        var window = new TrinixWindow(this, id, handle, options);
        windowsByHandle[handle] = window;
        windowsById[id] = window;
        windows.Add(window);

        // ⚠ The round trip is here, after the window is filed, and the order is the
        // whole reason WaylandClient.CreateWindow does not do it itself. The
        // configure that comes back is what says how big the window is and what
        // scale it is on; a callback that arrives before this dictionary knows the
        // handle finds nothing, drops the size, and leaves a window reporting the
        // one it asked for and a framebuffer of zero — which is a swapchain built at
        // the wrong size, or not at all.
        WaylandClient.Roundtrip(client);

        // And then the initial size as an event, so that an application which only
        // ever reads sizes from the stream gets one for a window it has just made
        // rather than having to special-case the first.
        events.Post(PlatformEvent.WindowResized(
            id, Timestamp(), window.ClientSizeCore, window.FramebufferSizeCore));

        return window;
    }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        if (windowsById.TryGetValue(id, out var found)) {
            window = found;
            return true;
        }

        window = null;
        return false;
    }

    /// <inheritdoc />
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Every callback the pump triggers posts into the buffer, so this reads as
        // one call and produces a frame's worth of events.
        if (!WaylandClient.Pump(client)) {
            // The compositor is gone. That is a quit, not an error: there is nothing
            // left to draw on and nothing to ask.
            lifecycle.RequestQuit();
            events.Post(PlatformEvent.Application(PlatformEventKind.Quit, Timestamp()));
        }

        return events.Drain();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Always false. Opening a URL means asking a handler to run, and Trinix's
    ///     is Phase 6's <c>trinix-open</c> — which launches *applications*, having
    ///     verified their signature, and has no notion of a scheme yet. Returning false
    ///     is the contract's way of saying the platform would not; launching a browser
    ///     that does not exist would be worse.
    /// </remarks>
    public bool TryOpenUrl(string url) => false;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // Copied, because disposing a window removes it from the list.
        foreach (var window in windows.ToArray()) {
            window.Dispose();
        }

        WaylandClient.Destroy(client);

        if (ReferenceEquals(s_current, this)) {
            s_current = null;
            DisplayHandle = IntPtr.Zero;
            ClientHandle = IntPtr.Zero;
        }
    }

    internal void Forget(TrinixWindow window, IntPtr handle) {
        windowsByHandle.Remove(handle);
        windowsById.Remove(window.Id);
        windows.Remove(window);

        // A window that overrode the application's menus takes the override
        // with it. The application's own bar is untouched — it belongs to the
        // connection, which is still up, and it is what the shell falls back
        // to the moment this window is gone.
        TrinixMenu.ForgetWindow(handle);
    }

    /// <summary>Milliseconds since this platform was constructed.</summary>
    /// <remarks>
    ///     Vixen's events carry a timestamp of its own choosing, and Wayland's are the
    ///     compositor's monotonic clock with an unspecified epoch. Mixing them would
    ///     produce a stream whose times do not order, so the compositor's are used for
    ///     nothing but distinguishing one event from the next.
    /// </remarks>
    long Timestamp() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    static unsafe WaylandClient.Callbacks BuildCallbacks() => new() {
        WindowConfigured = &OnWindowConfigured,
        WindowCloseRequested = &OnWindowCloseRequested,
        WindowFocusChanged = &OnWindowFocusChanged,
        Key = &OnKey,
        Text = &OnText,
        PointerMotion = &OnPointerMotion,
        PointerButton = &OnPointerButton,
        PointerScroll = &OnPointerScroll,
        PointerLeft = &OnPointerLeft,
        OutputChanged = &OnOutputChanged,
        OutputRemoved = &OnOutputRemoved,
        ControlActivated = &OnControlActivated,
        ControlHover = &OnControlHover,
        ShadowApplied = &OnShadowApplied,
        MenuActivated = &OnMenuActivated,
        MenuAboutToShow = &OnMenuAboutToShow,
        MenuClosed = &OnMenuClosed
    };

    // --- unmanaged entry points ---------------------------------------------
    //
    // Called from inside the pump. They translate and delegate and do nothing
    // else, because an exception thrown across the boundary into C is undefined
    // behaviour rather than a stack trace.

    [UnmanagedCallersOnly]
    static void OnWindowConfigured(IntPtr handle, int width, int height, int pixelWidth,
                                   int pixelHeight, int scale, uint mode) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        var size = new Int2(width, height);
        var pixelSize = new Int2(pixelWidth, pixelHeight);
        var hadScale = window.DpiScaleCore;
        var hadSize = window.ClientSizeCore;
        var wasMinimised = window.IsMinimised;

        window.Configured(size, pixelSize, scale, (WaylandClient.WindowMode)mode);

        // One configure can change several things and Vixen has an event for each.
        // Only what actually moved is reported: a resize event every frame would be
        // a swapchain rebuilt every frame.
        if (size != hadSize) {
            platform.events.Post(PlatformEvent.WindowResized(window.Id, platform.Timestamp(), size, pixelSize));
        }

        if (Math.Abs(window.DpiScaleCore - hadScale) > float.Epsilon) {
            platform.events.Post(PlatformEvent.WindowDpiChanged(window.Id, platform.Timestamp(), window.DpiScaleCore));
        }

        if (window.IsMinimised != wasMinimised) {
            platform.events.Post(PlatformEvent.Window(
                window.IsMinimised ? PlatformEventKind.WindowMinimised : PlatformEventKind.WindowRestored,
                window.Id,
                platform.Timestamp()));
        }
    }

    [UnmanagedCallersOnly]
    static void OnWindowCloseRequested(IntPtr handle) {
        if (TryFind(handle, out var platform, out var window)) {
            platform.events.Post(PlatformEvent.Window(
                PlatformEventKind.WindowCloseRequested, window.Id, platform.Timestamp()));
        }
    }

    [UnmanagedCallersOnly]
    static void OnWindowFocusChanged(IntPtr handle, byte focused) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        window.IsFocused = focused != 0;

        // See TrinixInput.ReleaseAll: a key held when focus leaves is never released
        // to this application, because the release goes wherever focus went.
        if (focused == 0) {
            platform.input.ReleaseAll();
        }

        platform.events.Post(PlatformEvent.Window(
            focused != 0 ? PlatformEventKind.WindowFocusGained : PlatformEventKind.WindowFocusLost,
            window.Id,
            platform.Timestamp()));
    }

    [UnmanagedCallersOnly]
    static void OnKey(IntPtr handle, uint code, byte pressed, uint modifiers, uint timeMsec) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        var key = EvdevKeys.Translate(code);
        if (key == Key.Unknown) {
            return;
        }

        var down = pressed != 0;
        var translated = TranslateModifiers(modifiers);

        platform.input.Modifiers = translated;
        platform.input.KeyChanged(key, down);

        platform.events.Post(PlatformEvent.Keyboard(
            down ? PlatformEventKind.KeyDown : PlatformEventKind.KeyUp,
            window.Id,
            platform.Timestamp(),
            key,
            translated));
    }

    [UnmanagedCallersOnly]
    static unsafe void OnText(IntPtr handle, byte* utf8) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        var text = WaylandClient.ReadString(utf8);
        if (!string.IsNullOrEmpty(text)) {
            platform.events.Post(PlatformEvent.TextInput(window.Id, platform.Timestamp(), text));
        }
    }

    [UnmanagedCallersOnly]
    static void OnPointerMotion(IntPtr handle, double x, double y, uint timeMsec) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        var position = new Vector2((float)x, (float)y);
        var delta = position - platform.input.PointerPosition;
        platform.input.PointerPosition = position;

        platform.events.Post(PlatformEvent.MouseMoved(
            window.Id, platform.Timestamp(), position, delta, platform.input.Modifiers));
    }

    [UnmanagedCallersOnly]
    static void OnPointerButton(IntPtr handle, uint button, byte pressed, uint timeMsec) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        var translated = TranslateButton(button);
        var down = pressed != 0;
        platform.input.ButtonChanged(translated, down);

        platform.events.Post(PlatformEvent.MouseButtonChanged(
            down ? PlatformEventKind.MouseButtonDown : PlatformEventKind.MouseButtonUp,
            window.Id,
            platform.Timestamp(),
            translated,
            platform.input.PointerPosition,
            clickCount: 1,
            platform.input.Modifiers));
    }

    [UnmanagedCallersOnly]
    static void OnPointerScroll(IntPtr handle, double dx, double dy, uint timeMsec) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        platform.events.Post(PlatformEvent.MouseWheel(
            window.Id,
            platform.Timestamp(),
            platform.input.PointerPosition,
            new Vector2((float)dx, (float)dy),
            platform.input.Modifiers));
    }

    [UnmanagedCallersOnly]
    static void OnPointerLeft(IntPtr handle) {
        if (TryFind(handle, out var platform, out var window)) {
            platform.events.Post(PlatformEvent.Window(
                PlatformEventKind.WindowMouseLeft, window.Id, platform.Timestamp()));
        }
    }

    [UnmanagedCallersOnly]
    static unsafe void OnOutputChanged(IntPtr output, int width, int height, int refreshMilliHertz,
                                       int scale, int x, int y, byte* name) {
        s_current?.displays.Changed(output, width, height, refreshMilliHertz, scale, x, y,
                                    WaylandClient.ReadString(name) ?? string.Empty);
    }

    [UnmanagedCallersOnly]
    static void OnOutputRemoved(IntPtr output) => s_current?.displays.Removed(output);

    /// <remarks>
    ///     ⚠ Close is turned into the same event <c>xdg_toplevel.close</c> produces,
    ///     which is what the protocol says it is: a request to the application, because
    ///     it may have something to ask about first. Minimise and zoom have no
    ///     <see cref="PlatformEvent" /> to become — Vixen has no event for "the user
    ///     asked to minimise" — so they are applied here, which is the one place in
    ///     this file that decides anything and is marked so it can be moved when the
    ///     shell has an opinion about them.
    /// </remarks>
    [UnmanagedCallersOnly]
    static void OnControlActivated(IntPtr handle, uint control) {
        if (!TryFind(handle, out var platform, out var window)) {
            return;
        }

        switch ((WaylandClient.Control)control) {
            case WaylandClient.Control.Close:
                platform.events.Post(PlatformEvent.Window(
                    PlatformEventKind.WindowCloseRequested, window.Id, platform.Timestamp()));
                break;

            case WaylandClient.Control.Minimise:
                WaylandClient.SetMode(handle, WaylandClient.WindowMode.Minimised);
                break;

            case WaylandClient.Control.Zoom:
                WaylandClient.SetMode(
                    handle,
                    window.ModeCore == WindowMode.Maximised
                        ? WaylandClient.WindowMode.Windowed
                        : WaylandClient.WindowMode.Maximised);
                break;

            default:
                break;
        }
    }

    /// <remarks>
    ///     Nothing yet. The compositor lights the traffic lights itself — that is the
    ///     point of it owning the hit zones — and this exists for a client that wants
    ///     to draw its own hover state, which no Trinix application does.
    /// </remarks>
    [UnmanagedCallersOnly]
    static void OnControlHover(IntPtr handle, uint control, uint state) { }

    /// <remarks>
    ///     The margins the compositor's shadow occupies. Recorded nowhere yet: it
    ///     matters to a client placing a popup relative to its own footprint, and
    ///     Trinix has no popups.
    /// </remarks>
    [UnmanagedCallersOnly]
    static void OnShadowApplied(IntPtr handle, int left, int top, int right, int bottom) { }

    [UnmanagedCallersOnly]
    static void OnMenuActivated(IntPtr handle, uint id) => TrinixMenu.Activated(handle, id);

    [UnmanagedCallersOnly]
    static void OnMenuAboutToShow(IntPtr handle, uint id) => TrinixMenu.AboutToShow(handle, id);

    [UnmanagedCallersOnly]
    static void OnMenuClosed(IntPtr handle) => TrinixMenu.Closed(handle);

    static bool TryFind(IntPtr handle, [NotNullWhen(true)] out TrinixPlatform? platform,
                        [NotNullWhen(true)] out TrinixWindow? window) {
        platform = s_current;
        window = null;

        return platform is not null && platform.windowsByHandle.TryGetValue(handle, out window);
    }

    /// <summary>
    ///     xkb's modifier masks, which are the numbers <c>trinix-menu-v1</c> uses, into
    ///     Vixen's.
    /// </summary>
    static KeyModifiers TranslateModifiers(uint mask) {
        var result = KeyModifiers.None;
        if ((mask & (uint)WaylandClient.Modifiers.Shift) != 0) { result |= KeyModifiers.Shift; }
        if ((mask & (uint)WaylandClient.Modifiers.Control) != 0) { result |= KeyModifiers.Control; }
        if ((mask & (uint)WaylandClient.Modifiers.Alt) != 0) { result |= KeyModifiers.Alt; }
        if ((mask & (uint)WaylandClient.Modifiers.Logo) != 0) { result |= KeyModifiers.Meta; }
        return result;
    }

    /// <summary>
    ///     Linux button codes into Vixen's.
    /// </summary>
    /// <remarks>
    ///     The numbers are <c>BTN_LEFT</c> and its neighbours from
    ///     <c>input-event-codes.h</c>, which start at 0x110 and are not in the order
    ///     anyone would guess: middle sits between left and right.
    /// </remarks>
    static MouseButton TranslateButton(uint button) => button switch {
        0x110 => MouseButton.Primary,
        0x111 => MouseButton.Secondary,
        0x112 => MouseButton.Middle,
        0x113 => MouseButton.Extra1,
        0x114 => MouseButton.Extra2,
        _ => MouseButton.None
    };
}

/// <summary>Who the application is, before it has a window.</summary>
/// <remarks>
///     Separate from Vixen's <c>UiApplicationOptions</c> on purpose: this platform is
///     usable by anything that wants an <see cref="IPlatform" />, including something
///     that is not a Vixen UI application, and taking Vixen's options type here would
///     make the UI framework a dependency of the window system.
/// </remarks>
public sealed class TrinixPlatformOptions {
    /// <summary>Who the application belongs to — half of where its settings live.</summary>
    public string Organisation { get; set; } = "Trinix";

    /// <summary>What the application is called — the other half.</summary>
    public string Application { get; set; } = "Application";

    /// <summary>
    ///     The identifier the shell groups this application's windows by.
    /// </summary>
    /// <remarks>
    ///     Reverse-DNS, and the same string as the bundle's <c>Contents/Info.json</c>
    ///     identity — <c>io.trinix.hello</c>. It is what a dock would collect windows
    ///     under, so two spellings is two icons for one application.
    /// </remarks>
    public string ApplicationId { get; set; } = "io.trinix.application";
}

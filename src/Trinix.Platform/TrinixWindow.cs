using Trinix.Interop;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     One window on Trinix: a <c>wl_surface</c> with an <c>xdg_toplevel</c> role.
/// </summary>
/// <remarks>
///     <para>
///         Almost every property here is a cache of something the compositor last
///         said, and that is not laziness — it is what Wayland is. A client cannot ask
///         how big it is or where it is; it is told, and between being told it knows
///         what it was told last.
///     </para>
///     <para>
///         The properties that would be questions elsewhere are therefore requests
///         here. Setting <see cref="ClientSize" /> asks; the compositor answers with a
///         configure, and only then does the property change. Code that sets a size and
///         reads it back on the next line will read the old one, on Trinix and on every
///         other Wayland system.
///     </para>
/// </remarks>
sealed class TrinixWindow : IWindow {
    readonly TrinixPlatform platform;
    readonly TrinixSurface surface;

    string title;
    bool resizable;
    CursorMode cursorMode = CursorMode.Normal;
    CursorShape cursorShape = CursorShape.Arrow;

    internal TrinixWindow(TrinixPlatform platform, uint id, IntPtr handle, in WindowOptions options) {
        this.platform = platform;
        Id = id;
        Handle = handle;
        title = options.Title;
        resizable = options.IsResizable;

        // The size asked for, until the compositor says otherwise — which it does
        // before the first frame, through the configure the platform collects with a
        // round trip immediately after this returns.
        //
        // ⚠ The framebuffer starts at the same number rather than at zero, and that
        // is not belt-and-braces: a swapchain is built from it, and a zero-sized one
        // is a device that fails to create rather than a window that looks wrong.
        ClientSizeCore = options.Size;
        FramebufferSizeCore = options.Size;
        DpiScaleCore = 1f;
        surface = new TrinixSurface(this);
    }

    /// <summary>The <c>trinix_wl_window</c> this wraps.</summary>
    internal IntPtr Handle { get; private set; }

    internal Int2 ClientSizeCore { get; private set; }

    internal Int2 FramebufferSizeCore { get; private set; }

    internal float DpiScaleCore { get; private set; }

    /// <inheritdoc />
    public uint Id { get; }

    /// <inheritdoc />
    public string Title {
        get => title;
        set {
            ArgumentNullException.ThrowIfNull(value);
            title = value;
            if (Handle != IntPtr.Zero) {
                WaylandClient.SetTitle(Handle, value);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Setting this is a request. See the type's own remarks for why it does not
    ///     take effect before the next read.
    /// </remarks>
    public Int2 ClientSize {
        get => ClientSizeCore;
        set {
            if (Handle == IntPtr.Zero) {
                return;
            }

            // The only way a Wayland client influences its own size is by
            // constraining it, so an exact size is a minimum and a maximum that
            // agree — and then relaxed again, or the window becomes unresizable.
            WaylandClient.SetMinimumSize(Handle, value.X, value.Y);
            WaylandClient.SetMaximumSize(Handle, value.X, value.Y);

            if (resizable) {
                WaylandClient.SetMinimumSize(Handle, 0, 0);
                WaylandClient.SetMaximumSize(Handle, 0, 0);
            }
        }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize => FramebufferSizeCore;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Always zero, and settable without effect. Wayland does not tell a client
    ///     where it is — deliberately, so that a window cannot place itself over a
    ///     password prompt — and
    ///     <see cref="PlatformCapabilities.WindowPositioning" /> is absent to say so.
    ///     Restoring a saved window position has to tolerate not happening.
    /// </remarks>
    public Int2 Position {
        get => Int2.Zero;
        set { }
    }

    /// <inheritdoc />
    public float DpiScale => DpiScaleCore;

    /// <inheritdoc />
    public WindowMode Mode {
        get => ModeCore;
        set {
            if (Handle != IntPtr.Zero) {
                WaylandClient.SetMode(Handle, Translate(value));
            }
        }
    }

    internal WindowMode ModeCore { get; private set; } = WindowMode.Windowed;

    /// <inheritdoc />
    public bool IsResizable {
        get => resizable;
        set {
            resizable = value;
            if (Handle == IntPtr.Zero) {
                return;
            }

            if (value) {
                WaylandClient.SetMinimumSize(Handle, 0, 0);
                WaylandClient.SetMaximumSize(Handle, 0, 0);
            } else {
                WaylandClient.SetMinimumSize(Handle, ClientSizeCore.X, ClientSizeCore.Y);
                WaylandClient.SetMaximumSize(Handle, ClientSizeCore.X, ClientSizeCore.Y);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A mapped Wayland surface is visible; there is no hidden-but-alive state
    ///     short of destroying the surface, which is what <see cref="Hide" /> does not
    ///     do.
    /// </remarks>
    public bool IsVisible => Handle != IntPtr.Zero && !IsClosed;

    /// <inheritdoc />
    public bool IsFocused { get; internal set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Its own state rather than a <see cref="WindowMode" />, because Vixen's
    ///     enum has no minimised member and xdg-shell has no minimised *state* — a
    ///     compositor that minimises a window simply stops sending it frames, and
    ///     says so by dropping the activated state rather than by naming one.
    /// </remarks>
    public bool IsMinimised { get; internal set; }

    /// <inheritdoc />
    public bool IsClosed { get; private set; }

    /// <inheritdoc />
    /// <remarks>Single-output for now: the compositor drives one display.</remarks>
    public int DisplayIndex => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     Accepted and remembered rather than applied. A Wayland client draws its own
    ///     cursor by attaching a buffer to the pointer's surface, which needs a buffer,
    ///     which needs the renderer this platform deliberately does not have —
    ///     <see cref="PlatformCapabilities.Cursor" /> is absent for that reason.
    /// </remarks>
    public CursorMode CursorMode {
        get => cursorMode;
        set => cursorMode = value;
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="CursorMode" />.</remarks>
    public CursorShape CursorShape {
        get => cursorShape;
        set => cursorShape = value;
    }

    /// <inheritdoc />
    public ISurface Surface => surface;

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing to do: the surface is shown by the compositor as soon as it has a
    ///     buffer, and the buffer comes from the swapchain rather than from here.
    /// </remarks>
    public void Show() { }

    /// <inheritdoc />
    /// <remarks>
    ///     Minimises instead, which is the nearest thing xdg-shell has. A surface can
    ///     be unmapped by attaching a null buffer, but only by whoever owns its
    ///     commits — and once there is a swapchain that is Mesa.
    /// </remarks>
    public void Hide() {
        if (Handle != IntPtr.Zero) {
            WaylandClient.SetMode(Handle, WaylandClient.WindowMode.Minimised);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Does nothing, and cannot. A Wayland client may not take focus; the
    ///     compositor gives it, in response to the user. An application that could
    ///     focus itself is an application that can steal a keystroke.
    /// </remarks>
    public void Focus() { }

    /// <inheritdoc />
    /// <remarks>Nothing to do: a client does not know or choose where it is.</remarks>
    public void Centre() { }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Does nothing yet, and the gap is real rather than structural. What this
    ///     asks for is <c>xdg-activation-v1</c>, which is also how a window raises
    ///     itself — and it needs a shell with somewhere to draw the attention, which
    ///     is Phase 5's dock.
    /// </remarks>
    public void RequestAttention() { }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Ignored. A window's icon on Trinix comes from its bundle — the same
    ///     <c>Contents/Resources</c> the launcher reads — rather than from pixels the
    ///     process hands over at runtime, so that a dock can show an icon for an
    ///     application that is not running. There is no protocol here to carry one.
    /// </remarks>
    public void SetIcon(ReadOnlySpan<byte> rgba, Int2 size) { }

    /// <inheritdoc />
    public void Dispose() {
        if (Handle == IntPtr.Zero) {
            return;
        }

        var handle = Handle;
        Handle = IntPtr.Zero;
        IsClosed = true;
        platform.Forget(this, handle);
        WaylandClient.DestroyWindow(handle);
    }

    /// <summary>Applies a configure the compositor sent.</summary>
    internal void Configured(Int2 size, Int2 pixelSize, int scale, WaylandClient.WindowMode mode) {
        ClientSizeCore = size;
        FramebufferSizeCore = pixelSize;
        DpiScaleCore = scale > 0 ? scale : 1;

        IsMinimised = mode == WaylandClient.WindowMode.Minimised;
        ModeCore = mode switch {
            WaylandClient.WindowMode.Maximised => WindowMode.Maximised,
            WaylandClient.WindowMode.Fullscreen => WindowMode.BorderlessFullscreen,
            _ => WindowMode.Windowed
        };
    }

    internal void Closed() => IsClosed = true;

    /// <remarks>
    ///     Both fullscreen modes map to one request. xdg-shell has a single
    ///     fullscreen state and no notion of taking a display exclusively — there is
    ///     no mode-set to ask for, because the compositor owns the mode.
    /// </remarks>
    static WaylandClient.WindowMode Translate(WindowMode mode) => mode switch {
        WindowMode.Maximised => WaylandClient.WindowMode.Maximised,
        WindowMode.BorderlessFullscreen or WindowMode.ExclusiveFullscreen =>
            WaylandClient.WindowMode.Fullscreen,
        _ => WaylandClient.WindowMode.Windowed
    };
}

/// <summary>
///     What a graphics backend presents to: the two libwayland pointers Vulkan's
///     <c>VK_KHR_wayland_surface</c> takes.
/// </summary>
/// <remarks>
///     ⚠ The display is the one libwayland made, not a handle this code invented.
///     Mesa's WSI calls libwayland on it — creating its own event queue, marshalling
///     its own requests — which is the constraint that decided the whole shape of
///     <c>trinix-wl-client</c>.
/// </remarks>
sealed class TrinixSurface(TrinixWindow window) : ISurface {
    /// <inheritdoc />
    public SurfaceHandle Handle => new(
        SurfaceKind.Wayland,
        TrinixPlatform.DisplayHandle,
        window.Handle == IntPtr.Zero ? IntPtr.Zero : WaylandClient.Surface(window.Handle)
    );

    /// <inheritdoc />
    public Int2 PixelSize => window.FramebufferSizeCore;
}

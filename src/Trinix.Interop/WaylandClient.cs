using System.Runtime.InteropServices;

namespace Trinix.Interop;

/// <summary>
///     Bindings for <c>libtrinix-wl-client</c>, the C library that owns Trinix's
///     Wayland client objects.
/// </summary>
/// <remarks>
///     <para>
///         The mirror image of <see cref="Wlroots" />, on the other side of the wire.
///         Everything said there about handles applies here: they are
///         <see cref="IntPtr" />, they are never dereferenced, and the C library's own
///         header — <c>base/recipes/trinix-wl-client/src/trinix-wl-client.h</c> — is
///         the normative version of everything declared below.
///     </para>
///     <para>
///         <b>Why there is a C library at all on this side.</b> libwayland's client
///         API is not a wire format that C# could speak instead: every request goes
///         through a variadic <c>wl_proxy_marshal_flags</c> against interface tables
///         that encode each argument's type, and a mistranscribed signature does not
///         fail — it marshals the next argument as the wrong type and the compositor
///         disconnects with a protocol error naming an opcode. Underneath that,
///         Vulkan's WSI takes a libwayland <c>wl_display</c> and calls libwayland on
///         it, so the display object has to be libwayland's whatever else is decided.
///     </para>
///     <para>
///         Every callback runs inside <see cref="Pump" />, on the thread that called
///         it, never re-entrantly — which is what lets the platform turn each one into
///         an event without a lock.
///     </para>
/// </remarks>
public static unsafe partial class WaylandClient {
    const string Library = "trinix-wl-client";

    /// <summary>Connects to the compositor and binds what it offers.</summary>
    /// <param name="callbacks">Handlers. Copied by the callee; nothing is retained.</param>
    /// <param name="userData">Passed back untouched. May be <see cref="IntPtr.Zero" />.</param>
    /// <returns>
    ///     A client handle, or <see cref="IntPtr.Zero" /> when there is no compositor
    ///     to talk to — which is the ordinary case for a process started outside a
    ///     session, and is a platform that cannot do windowing rather than an error.
    /// </returns>
    public static IntPtr Connect(in Callbacks callbacks, IntPtr userData) {
        fixed (Callbacks* pointer = &callbacks) {
            return NativeConnect(pointer, userData);
        }
    }

    /// <summary>Disconnects and frees everything.</summary>
    /// <param name="client">A handle from <see cref="Connect" />.</param>
    public static void Destroy(IntPtr client) => NativeDestroy(client);

    /// <summary>Which optional globals the compositor offered.</summary>
    /// <param name="client">A connected client.</param>
    /// <returns>A mask; a missing global is a capability, not a failure.</returns>
    public static Globals AvailableGlobals(IntPtr client) => (Globals)NativeGlobals(client);

    /// <summary>
    ///     Reads what the compositor has sent, calls back for each of it, and flushes
    ///     what this side has queued. Never blocks.
    /// </summary>
    /// <param name="client">A connected client.</param>
    /// <returns>
    ///     <see langword="false" /> once the connection is gone, which is how a
    ///     compositor exiting reaches the application.
    /// </returns>
    /// <remarks>
    ///     Dispatches the default queue only. Mesa's WSI runs the swapchain on a queue
    ///     of its own and drains it inside <c>vkAcquireNextImageKHR</c> and
    ///     <c>vkQueuePresentKHR</c>, so the two do not race.
    /// </remarks>
    public static bool Pump(IntPtr client) => NativePump(client);

    /// <summary>Sends everything queued and waits for the compositor to answer it.</summary>
    /// <param name="client">A connected client.</param>
    /// <returns><see langword="false" /> if the connection died waiting.</returns>
    /// <remarks>
    ///     ⚠ Blocks, unlike <see cref="Pump" />. Used once per window, right after
    ///     <see cref="CreateWindow" />, to collect the configure that names its size —
    ///     see that method for why the two are separate calls.
    /// </remarks>
    public static bool Roundtrip(IntPtr client) => NativeRoundtrip(client);

    /// <summary>The <c>wl_display</c> for <c>VkWaylandSurfaceCreateInfoKHR</c>.</summary>
    /// <param name="client">A connected client.</param>
    public static IntPtr Display(IntPtr client) => NativeDisplay(client);

    /// <summary>The <c>wl_surface</c> for <c>VkWaylandSurfaceCreateInfoKHR</c>.</summary>
    /// <param name="window">A window from <see cref="CreateWindow" />.</param>
    public static IntPtr Surface(IntPtr window) => NativeWindowSurface(window);

    /// <summary>Creates a surface with an <c>xdg_toplevel</c> role.</summary>
    /// <param name="client">A connected client.</param>
    /// <param name="title">The window title.</param>
    /// <param name="appId">The application identifier the shell groups windows by.</param>
    /// <param name="width">The size to ask for, in logical pixels.</param>
    /// <param name="height">The size to ask for, in logical pixels.</param>
    /// <param name="resizable">Whether the user may resize it.</param>
    /// <returns>The window, carrying the size that was asked for rather than the one
    /// it will have.</returns>
    /// <remarks>
    ///     ⚠ Does not wait for the compositor's configure — <see cref="Roundtrip" />
    ///     is how, and the caller must, before anything attaches a buffer. They are
    ///     two calls so that the window can be registered in between: the configure
    ///     names a window, and a callback for one nothing has filed yet drops the size
    ///     it carries.
    /// </remarks>
    public static IntPtr CreateWindow(
        IntPtr client,
        string title,
        string appId,
        int width,
        int height,
        bool resizable
    ) => NativeWindowCreate(client, title, appId, width, height, resizable);

    /// <summary>Destroys a window.</summary>
    /// <param name="window">The window.</param>
    public static void DestroyWindow(IntPtr window) => NativeWindowDestroy(window);

    /// <summary>Renames a window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="title">The new title.</param>
    public static void SetTitle(IntPtr window, string title) => NativeWindowSetTitle(window, title);

    /// <summary>Asks the compositor to change the window's mode.</summary>
    /// <param name="window">The window.</param>
    /// <param name="mode">What to ask for.</param>
    /// <remarks>
    ///     A request, not a change. The compositor decides and says so with a
    ///     configure, which is why nothing observable happens here.
    /// </remarks>
    public static void SetMode(IntPtr window, WindowMode mode) =>
        NativeWindowSetMode(window, (uint)mode);

    /// <summary>Constrains how small the window may be made.</summary>
    /// <param name="window">The window.</param>
    /// <param name="width">Minimum width in logical pixels, or zero for none.</param>
    /// <param name="height">Minimum height in logical pixels, or zero for none.</param>
    public static void SetMinimumSize(IntPtr window, int width, int height) =>
        NativeWindowSetMinSize(window, width, height);

    /// <summary>Constrains how large the window may be made.</summary>
    /// <param name="window">The window.</param>
    /// <param name="width">Maximum width in logical pixels, or zero for none.</param>
    /// <param name="height">Maximum height in logical pixels, or zero for none.</param>
    public static void SetMaximumSize(IntPtr window, int width, int height) =>
        NativeWindowSetMaxSize(window, width, height);

    /// <summary>Starts an interactive move, as if the user had grabbed the window.</summary>
    /// <param name="window">The window.</param>
    /// <remarks>
    ///     ⚠ Only works from inside a button handler. Wayland authorises the gesture
    ///     by the serial of the click that began it, and there is no other way for a
    ///     client to position itself.
    /// </remarks>
    public static void BeginMove(IntPtr window) => NativeWindowBeginMove(window);

    /// <summary>Starts an interactive resize from the given edges.</summary>
    /// <param name="window">The window.</param>
    /// <param name="edges">An <c>xdg_toplevel.resize_edge</c> value.</param>
    public static void BeginResize(IntPtr window, uint edges) =>
        NativeWindowBeginResize(window, edges);

    /// <summary>Asks the compositor for a drop shadow.</summary>
    /// <param name="window">The window.</param>
    /// <param name="style">Which shadow.</param>
    /// <remarks>Does nothing without <see cref="Globals.Shell" />.</remarks>
    public static void SetShadow(IntPtr window, ShadowStyle style) =>
        NativeWindowSetShadow(window, (uint)style);

    /// <summary>Rounds the window's corners, in surface-local pixels.</summary>
    /// <param name="window">The window.</param>
    /// <param name="radius">The radius; zero is square.</param>
    public static void SetCornerRadius(IntPtr window, int radius) =>
        NativeWindowSetCornerRadius(window, radius);

    /// <summary>Declares where dragging moves the window — the title bar.</summary>
    /// <param name="window">The window.</param>
    /// <param name="x">Surface-local left edge.</param>
    /// <param name="y">Surface-local top edge.</param>
    /// <param name="width">Width, or zero to clear the region.</param>
    /// <param name="height">Height, or zero to clear the region.</param>
    /// <remarks>
    ///     Motion inside the region never reaches the application, which is what keeps
    ///     dragging smooth while its own thread is busy.
    /// </remarks>
    public static void SetDragRegion(IntPtr window, int x, int y, int width, int height) =>
        NativeWindowSetDragRegion(window, x, y, width, height);

    /// <summary>How far inside its own edges a resize grab starts.</summary>
    /// <param name="window">The window.</param>
    /// <param name="inset">The inset in surface-local pixels.</param>
    public static void SetResizeInset(IntPtr window, int inset) =>
        NativeWindowSetResizeInset(window, inset);

    /// <summary>Declares one traffic light's hit zone.</summary>
    /// <param name="window">The window.</param>
    /// <param name="control">Which control.</param>
    /// <param name="x">Surface-local left edge.</param>
    /// <param name="y">Surface-local top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    public static void SetControl(IntPtr window, Control control, int x, int y, int width, int height) =>
        NativeWindowSetControl(window, (uint)control, x, y, width, height);

    /// <summary>Removes a control's hit zone.</summary>
    /// <param name="window">The window.</param>
    /// <param name="control">Which control.</param>
    public static void UnsetControl(IntPtr window, Control control) =>
        NativeWindowUnsetControl(window, (uint)control);

    /// <summary>Takes over this application's menu bar.</summary>
    /// <remarks>
    ///     <para>
    ///         The ordinary way to get a menu bar, and the only one most applications
    ///         want. It covers every window this connection opens, it does not change
    ///         as the user moves between them, and it exists while there are none —
    ///         which is how an application with everything closed still offers
    ///         File ▸ New.
    ///     </para>
    ///     <para>
    ///         Idempotent: asking twice is a protocol error, and the library answers
    ///         with the bar it already made rather than committing one.
    ///     </para>
    /// </remarks>
    /// <param name="client">A connected client.</param>
    /// <returns>
    ///     A menu handle, or <see cref="IntPtr.Zero" /> when the compositor offered no
    ///     <c>trinix_menu_manager_v1</c> — a system with no menu bar, and an
    ///     application that is otherwise fine.
    /// </returns>
    public static IntPtr CreateApplicationMenu(IntPtr client) => NativeClientMenuCreate(client);

    /// <summary>Gives one window a menu bar of its own.</summary>
    /// <remarks>
    ///     ⚠ The exception, not the shorthand. The shell shows this in place of the
    ///     application's bar while this window is active, which is right for a window
    ///     whose menus genuinely are not the application's and wrong for every other
    ///     window — asking per window exports one tree N times and leaves the
    ///     application applying every state change N times.
    /// </remarks>
    /// <param name="window">The window whose menus differ.</param>
    /// <returns>A menu handle, or <see cref="IntPtr.Zero" /> if there is no menu bar.</returns>
    public static IntPtr CreateWindowMenu(IntPtr window) => NativeWindowMenuCreate(window);

    /// <summary>Withdraws a menu bar.</summary>
    /// <param name="menu">A menu handle.</param>
    public static void DestroyMenu(IntPtr menu) => NativeMenuDestroy(menu);

    /// <summary>Adds an item to a menu bar.</summary>
    /// <param name="menu">The menu.</param>
    /// <param name="id">Client-assigned, non-zero, unique within this menu.</param>
    /// <param name="parent">The containing submenu, or zero for a top-level menu.</param>
    /// <param name="index">Where among its siblings.</param>
    /// <param name="kind">Item, separator, submenu, checkbox or radio.</param>
    /// <param name="label">The text, with an underscore before the mnemonic.</param>
    /// <remarks>Takes effect at <see cref="CommitMenu" />, not here.</remarks>
    public static void InsertMenuItem(
        IntPtr menu,
        uint id,
        uint parent,
        int index,
        MenuKind kind,
        string label
    ) => NativeMenuInsert(menu, id, parent, index, (uint)kind, label);

    /// <summary>Changes an item's label or state.</summary>
    /// <param name="menu">The menu.</param>
    /// <param name="id">The item.</param>
    /// <param name="label">The new text.</param>
    /// <param name="state">Enabled, checked, hidden.</param>
    public static void UpdateMenuItem(IntPtr menu, uint id, string label, MenuState state) =>
        NativeMenuUpdate(menu, id, label, (uint)state);

    /// <summary>Gives an item a keyboard accelerator the shell handles.</summary>
    /// <param name="menu">The menu.</param>
    /// <param name="id">The item.</param>
    /// <param name="keysym">An xkb keysym.</param>
    /// <param name="modifiers">The modifier mask.</param>
    /// <remarks>
    ///     A registered accelerator never reaches the application as a key press —
    ///     which is the point of registering it.
    /// </remarks>
    public static void SetMenuAccelerator(IntPtr menu, uint id, uint keysym, Modifiers modifiers) =>
        NativeMenuAccelerator(menu, id, keysym, (uint)modifiers);

    /// <summary>Removes an item and everything under it.</summary>
    /// <param name="menu">The menu.</param>
    /// <param name="id">The item.</param>
    public static void RemoveMenuItem(IntPtr menu, uint id) => NativeMenuRemove(menu, id);

    /// <summary>Applies everything since the last commit.</summary>
    /// <param name="menu">The menu.</param>
    /// <remarks>A commit with nothing pending is valid and does nothing.</remarks>
    public static void CommitMenu(IntPtr menu) => NativeMenuCommit(menu);

    /// <summary>Reads a NUL-terminated UTF-8 string a callback was given.</summary>
    /// <param name="value">A pointer, possibly null.</param>
    /// <returns>The string, or <see langword="null" />.</returns>
    /// <remarks>
    ///     The same three lines as <see cref="Wlroots.ReadString" />, and deliberately
    ///     not shared with it: the two classes are the two sides of one boundary and
    ///     each is meant to be readable on its own, which a helper class holding one
    ///     expression would not improve.
    /// </remarks>
    public static string? ReadString(byte* value) =>
        value is null ? null : Marshal.PtrToStringUTF8((IntPtr)value);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_connect")]
    private static partial IntPtr NativeConnect(Callbacks* callbacks, IntPtr userData);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_destroy")]
    private static partial void NativeDestroy(IntPtr client);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_globals")]
    private static partial uint NativeGlobals(IntPtr client);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_pump")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool NativePump(IntPtr client);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_roundtrip")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool NativeRoundtrip(IntPtr client);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_display")]
    private static partial IntPtr NativeDisplay(IntPtr client);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_surface")]
    private static partial IntPtr NativeWindowSurface(IntPtr window);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr NativeWindowCreate(
        IntPtr client,
        string title,
        string appId,
        int width,
        int height,
        [MarshalAs(UnmanagedType.U1)] bool resizable
    );

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_destroy")]
    private static partial void NativeWindowDestroy(IntPtr window);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_title", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void NativeWindowSetTitle(IntPtr window, string title);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_mode")]
    private static partial void NativeWindowSetMode(IntPtr window, uint mode);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_min_size")]
    private static partial void NativeWindowSetMinSize(IntPtr window, int width, int height);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_max_size")]
    private static partial void NativeWindowSetMaxSize(IntPtr window, int width, int height);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_begin_move")]
    private static partial void NativeWindowBeginMove(IntPtr window);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_begin_resize")]
    private static partial void NativeWindowBeginResize(IntPtr window, uint edges);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_shadow")]
    private static partial void NativeWindowSetShadow(IntPtr window, uint style);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_corner_radius")]
    private static partial void NativeWindowSetCornerRadius(IntPtr window, int radius);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_drag_region")]
    private static partial void NativeWindowSetDragRegion(IntPtr window, int x, int y, int width, int height);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_resize_inset")]
    private static partial void NativeWindowSetResizeInset(IntPtr window, int inset);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_set_control")]
    private static partial void NativeWindowSetControl(
        IntPtr window,
        uint control,
        int x,
        int y,
        int width,
        int height
    );

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_unset_control")]
    private static partial void NativeWindowUnsetControl(IntPtr window, uint control);

    [LibraryImport(Library, EntryPoint = "trinix_wl_client_menu_create")]
    private static partial IntPtr NativeClientMenuCreate(IntPtr client);

    [LibraryImport(Library, EntryPoint = "trinix_wl_window_menu_create")]
    private static partial IntPtr NativeWindowMenuCreate(IntPtr window);

    [LibraryImport(Library, EntryPoint = "trinix_wl_menu_destroy")]
    private static partial void NativeMenuDestroy(IntPtr menu);

    [LibraryImport(Library, EntryPoint = "trinix_wl_menu_insert", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void NativeMenuInsert(
        IntPtr menu,
        uint id,
        uint parent,
        int index,
        uint kind,
        string label
    );

    [LibraryImport(Library, EntryPoint = "trinix_wl_menu_update", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void NativeMenuUpdate(IntPtr menu, uint id, string label, uint state);

    [LibraryImport(Library, EntryPoint = "trinix_wl_menu_accelerator")]
    private static partial void NativeMenuAccelerator(IntPtr menu, uint id, uint keysym, uint modifiers);

    [LibraryImport(Library, EntryPoint = "trinix_wl_menu_remove")]
    private static partial void NativeMenuRemove(IntPtr menu, uint id);

    [LibraryImport(Library, EntryPoint = "trinix_wl_menu_commit")]
    private static partial void NativeMenuCommit(IntPtr menu);

    /// <summary>Which optional globals a compositor offered.</summary>
    [Flags]
    public enum Globals : uint {
        /// <summary>None of them, which is a functioning but undecorated window.</summary>
        None = 0,

        /// <summary><c>trinix_shell_v1</c>: shadows, corners, drag regions, traffic lights.</summary>
        Shell = 1 << 0,

        /// <summary><c>trinix_menu_manager_v1</c>: the global menu bar.</summary>
        Menu = 1 << 1,

        /// <summary><c>wl_data_device_manager</c>: clipboard and drag-and-drop.</summary>
        DataDevice = 1 << 2
    }

    /// <summary>What a window is doing with the screen.</summary>
    public enum WindowMode : uint {
        /// <summary>An ordinary window.</summary>
        Windowed = 0,

        /// <summary>Hidden, to a dock that does not exist yet.</summary>
        Minimised = 1,

        /// <summary>Filling the work area.</summary>
        Maximised = 2,

        /// <summary>Filling the output.</summary>
        Fullscreen = 3
    }

    /// <summary>The shadow styles <c>trinix-shell-v1</c> defines.</summary>
    public enum ShadowStyle : uint {
        /// <summary>No shadow.</summary>
        None = 0,

        /// <summary>A tight shadow, for panels and menus.</summary>
        Docked = 1,

        /// <summary>What an ordinary window casts.</summary>
        Window = 2,

        /// <summary>A deeper shadow, for dialogs and popovers.</summary>
        Floating = 3
    }

    /// <summary>The traffic lights.</summary>
    public enum Control : uint {
        /// <summary>Dismiss the window.</summary>
        Close = 0,

        /// <summary>Hide it.</summary>
        Minimise = 1,

        /// <summary>Toggle its ideal size.</summary>
        Zoom = 2
    }

    /// <summary>What the pointer is doing to a control group.</summary>
    public enum HoverState : uint {
        /// <summary>No longer over any control.</summary>
        Left = 0,

        /// <summary>Over the named control.</summary>
        Entered = 1,

        /// <summary>Holding the named control down.</summary>
        Pressed = 2
    }

    /// <summary>What a menu entry is.</summary>
    public enum MenuKind : uint {
        /// <summary>An ordinary item.</summary>
        Item = 0,

        /// <summary>A divider; the label is ignored.</summary>
        Separator = 1,

        /// <summary>A container for further items.</summary>
        Submenu = 2,

        /// <summary>An item with an independent on/off state.</summary>
        Checkbox = 3,

        /// <summary>One of a set; siblings of the same kind are exclusive.</summary>
        Radio = 4
    }

    /// <summary>How a menu item is presented.</summary>
    [Flags]
    public enum MenuState : uint {
        /// <summary>Not selectable, not ticked, shown.</summary>
        None = 0,

        /// <summary>Selectable.</summary>
        Enabled = 1,

        /// <summary>Ticked.</summary>
        Checked = 2,

        /// <summary>In the model but not shown.</summary>
        Hidden = 4
    }

    /// <summary>
    ///     Accelerator modifiers, which are xkb's masks and the same numbers the
    ///     <see cref="Callbacks.Key" /> callback reports.
    /// </summary>
    [Flags]
    public enum Modifiers : uint {
        /// <summary>None held.</summary>
        None = 0,

        /// <summary>Shift.</summary>
        Shift = 1,

        /// <summary>Control.</summary>
        Control = 4,

        /// <summary>Alt.</summary>
        Alt = 8,

        /// <summary>The Command key.</summary>
        Logo = 64
    }

    /// <summary>
    ///     Everything the client is told about.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Field order is the ABI.</b> This must match
    ///     <c>struct trinix_wl_callbacks</c> field for field; a reordering compiles on
    ///     both sides and calls the wrong function through the right name.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks {
        /// <summary>
        ///     The compositor decided how big the window is: window, logical width and
        ///     height, pixel width and height, scale, mode.
        /// </summary>
        public delegate* unmanaged<IntPtr, int, int, int, int, int, uint, void> WindowConfigured;

        /// <summary>The user asked to close a window.</summary>
        public delegate* unmanaged<IntPtr, void> WindowCloseRequested;

        /// <summary>A window gained or lost keyboard focus.</summary>
        public delegate* unmanaged<IntPtr, byte, void> WindowFocusChanged;

        /// <summary>
        ///     A key changed state: window, evdev code, pressed, modifiers, time. The
        ///     code is a physical position and never a keysym.
        /// </summary>
        public delegate* unmanaged<IntPtr, uint, byte, uint, uint, void> Key;

        /// <summary>Text was typed, already composed: window, UTF-8.</summary>
        public delegate* unmanaged<IntPtr, byte*, void> Text;

        /// <summary>The pointer moved: window, surface-local x and y, time.</summary>
        public delegate* unmanaged<IntPtr, double, double, uint, void> PointerMotion;

        /// <summary>A pointer button changed: window, evdev button, pressed, time.</summary>
        public delegate* unmanaged<IntPtr, uint, byte, uint, void> PointerButton;

        /// <summary>The wheel turned: window, horizontal and vertical amounts, time.</summary>
        public delegate* unmanaged<IntPtr, double, double, uint, void> PointerScroll;

        /// <summary>The pointer left a window.</summary>
        public delegate* unmanaged<IntPtr, void> PointerLeft;

        /// <summary>
        ///     An output appeared or changed: handle, width, height, refresh in mHz,
        ///     scale, x, y, name.
        /// </summary>
        public delegate* unmanaged<IntPtr, int, int, int, int, int, int, byte*, void> OutputChanged;

        /// <summary>An output went away. The handle is invalid after this returns.</summary>
        public delegate* unmanaged<IntPtr, void> OutputRemoved;

        /// <summary>The user operated a traffic light: window, control.</summary>
        public delegate* unmanaged<IntPtr, uint, void> ControlActivated;

        /// <summary>The pointer entered, moved within or left the controls.</summary>
        public delegate* unmanaged<IntPtr, uint, uint, void> ControlHover;

        /// <summary>What the shadow occupies outside the window: left, top, right, bottom.</summary>
        public delegate* unmanaged<IntPtr, int, int, int, int, void> ShadowApplied;

        /// <summary>The user chose a menu item: menu, item id. Leaf items only.</summary>
        /// <remarks>
        ///     ⚠ The handle is the menu, not a window. A menu bar belongs to the
        ///     application and there is often no window it could name — an
        ///     application with everything closed still has one, and an item chosen
        ///     from it is how a window comes back.
        /// </remarks>
        public delegate* unmanaged<IntPtr, uint, void> MenuActivated;

        /// <summary>A submenu is opening, which is where to populate it lazily.</summary>
        public delegate* unmanaged<IntPtr, uint, void> MenuAboutToShow;

        /// <summary>The menu session ended, however it ended.</summary>
        public delegate* unmanaged<IntPtr, void> MenuClosed;
    }
}

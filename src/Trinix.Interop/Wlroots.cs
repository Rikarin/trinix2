using System.Runtime.InteropServices;

namespace Trinix.Interop;

/// <summary>
///     Bindings for <c>libtrinix-wlr</c>, the C library that owns Trinix's wlroots
///     objects.
/// </summary>
/// <remarks>
///     <para>
///         Handles are <see cref="IntPtr" /> and are never dereferenced here. That is
///         the whole design: wlroots' ABI is explicitly unstable, so the managed side
///         knows only function names, and a version bump is a recompile of the C
///         library rather than a re-derivation of struct offsets.
///     </para>
///     <para>
///         The <c>extern</c> declarations are private and every public member is a
///         wrapper around one. That is not ceremony: it is what keeps <c>byte*</c> and
///         raw booleans out of the surface the compositor codes against, so the only
///         file in the tree that has to think about marshalling is this one.
///     </para>
///     <para>
///         Every callback runs on the Wayland event loop's thread. There is exactly
///         one such thread, and it is the same thread that called
///         <see cref="Run(IntPtr)" />, so callback bodies need no synchronisation — and
///         must not block, because nothing else will be serviced while they do.
///     </para>
/// </remarks>
public static unsafe partial class Wlroots {
    const string Library = "trinix-wlr";

    /// <summary>
    ///     Creates the compositor's wlroots objects.
    /// </summary>
    /// <param name="callbacks">Handlers. Copied by the callee; nothing is retained.</param>
    /// <param name="logLevel">wlroots' own logging verbosity.</param>
    /// <returns>
    ///     An opaque server handle, or <see cref="IntPtr.Zero" /> if there is no
    ///     display or no seat to open one with.
    /// </returns>
    public static IntPtr Create(in Callbacks callbacks, LogLevel logLevel) {
        fixed (Callbacks* pointer = &callbacks) {
            return NativeCreate(pointer, (int)logLevel);
        }
    }

    /// <summary>
    ///     Opens the Wayland socket and starts the backend — the point at which DRM
    ///     master is taken and outputs appear.
    /// </summary>
    /// <param name="server">A handle from <see cref="Create" />.</param>
    /// <param name="socketName">
    ///     The display name to listen on, or <see langword="null" /> for the first
    ///     free one. A fixed name fails rather than falling back, which is what
    ///     makes it safe for a unit file to name the display in advance.
    /// </param>
    /// <returns><see langword="true" /> if the compositor can run.</returns>
    public static bool Start(IntPtr server, string? socketName) => NativeStart(server, socketName);

    /// <summary>The display name clients need in <c>WAYLAND_DISPLAY</c>.</summary>
    /// <param name="server">A started server.</param>
    /// <returns>The socket name, or an empty string before <see cref="Start" />.</returns>
    public static string SocketName(IntPtr server) => ReadString(NativeSocket(server)) ?? string.Empty;

    /// <summary>Runs the event loop until <see cref="Terminate" /> is called.</summary>
    /// <param name="server">A started server.</param>
    public static void Run(IntPtr server) => NativeRun(server);

    /// <summary>Asks the event loop to return.</summary>
    /// <param name="server">A running server.</param>
    public static void Terminate(IntPtr server) => NativeTerminate(server);

    /// <summary>Disconnects clients and releases everything.</summary>
    /// <param name="server">A server that is no longer running.</param>
    public static void Destroy(IntPtr server) => NativeDestroy(server);

    /// <summary>Current resolution of an output.</summary>
    /// <param name="output">An output handle.</param>
    /// <param name="width">Receives the width in pixels.</param>
    /// <param name="height">Receives the height in pixels.</param>
    public static void OutputSize(IntPtr output, out int width, out int height) =>
        NativeOutputSize(output, out width, out height);

    /// <summary>The window's title, or null if the client has not set one.</summary>
    /// <param name="toplevel">A window handle.</param>
    /// <returns>The title, or <see langword="null" />.</returns>
    public static string? ToplevelTitle(IntPtr toplevel) => ReadString(NativeToplevelTitle(toplevel));

    /// <summary>The window's application identifier, or null.</summary>
    /// <param name="toplevel">A window handle.</param>
    /// <returns>The app id, or <see langword="null" />.</returns>
    public static string? ToplevelAppId(IntPtr toplevel) => ReadString(NativeToplevelAppId(toplevel));

    /// <summary>Moves a window in layout coordinates.</summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="x">New left edge.</param>
    /// <param name="y">New top edge.</param>
    public static void ToplevelSetPosition(IntPtr toplevel, int x, int y) => NativeToplevelSetPosition(toplevel, x, y);

    /// <summary>
    ///     Asks a window to take a new size. The client decides when to comply, so
    ///     the geometry reported by <see cref="ToplevelGetBox" /> lags this call.
    /// </summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="width">Requested width, or zero to let the client choose.</param>
    /// <param name="height">Requested height, or zero to let the client choose.</param>
    public static void ToplevelSetSize(IntPtr toplevel, int width, int height) =>
        NativeToplevelSetSize(toplevel, width, height);

    /// <summary>Position and current size of a window.</summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="x">Receives the left edge.</param>
    /// <param name="y">Receives the top edge.</param>
    /// <param name="width">Receives the visible width.</param>
    /// <param name="height">Receives the visible height.</param>
    public static void ToplevelGetBox(
        IntPtr toplevel,
        out int x,
        out int y,
        out int width,
        out int height
    ) =>
        NativeToplevelGetBox(toplevel, out x, out y, out width, out height);

    /// <summary>Raises a window, activates it and gives it keyboard focus.</summary>
    /// <param name="toplevel">A window handle.</param>
    public static void ToplevelFocus(IntPtr toplevel) => NativeToplevelFocus(toplevel);

    /// <summary>Asks a window to close. A client may decline.</summary>
    /// <param name="toplevel">A window handle.</param>
    public static void ToplevelClose(IntPtr toplevel) => NativeToplevelClose(toplevel);

    /// <summary>Where the pointer is, in layout coordinates.</summary>
    /// <param name="server">A running server.</param>
    /// <param name="x">Receives the horizontal position.</param>
    /// <param name="y">Receives the vertical position.</param>
    public static void CursorPosition(IntPtr server, out double x, out double y) =>
        NativeCursorPosition(server, out x, out y);

    /// <summary>Topmost window at these layout coordinates.</summary>
    /// <param name="server">A running server.</param>
    /// <param name="x">Horizontal position.</param>
    /// <param name="y">Vertical position.</param>
    /// <returns>A window handle, or <see cref="IntPtr.Zero" />.</returns>
    public static IntPtr ToplevelAt(IntPtr server, double x, double y) => NativeToplevelAt(server, x, y);

    /// <summary>
    ///     Sends the pointer's position to whatever is under it, or clears pointer
    ///     focus if that is nothing. The normal response to motion when the
    ///     compositor is not itself dragging something.
    /// </summary>
    /// <param name="server">A running server.</param>
    /// <param name="timeMsec">The event's timestamp, forwarded to the client.</param>
    public static void PointerPassthrough(IntPtr server, uint timeMsec) => NativePointerPassthrough(server, timeMsec);

    /// <summary>
    ///     Which control's hit zone contains this point, in window-geometry-local
    ///     coordinates, or <see cref="WindowControl.None" />.
    /// </summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="x">Horizontal position within the window.</param>
    /// <param name="y">Vertical position within the window.</param>
    /// <returns>The control under the point.</returns>
    public static WindowControl ToplevelControlAt(IntPtr toplevel, int x, int y) =>
        (WindowControl)NativeToplevelControlAt(toplevel, x, y);

    /// <summary>Tells a client the pointer entered, moved between, or left its controls.</summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="control">The control under the pointer.</param>
    /// <param name="state">What the pointer is doing.</param>
    public static void ToplevelSendControlHover(
        IntPtr toplevel,
        WindowControl control,
        ControlHover state
    ) =>
        NativeToplevelSendControlHover(toplevel, (int)control, (uint)state);

    /// <summary>Tells a client the user operated one of its controls.</summary>
    /// <remarks>
    ///     A request, not an instruction: closing is the application's decision,
    ///     exactly as with <c>xdg_toplevel.close</c>.
    /// </remarks>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="control">The control that was operated.</param>
    public static void ToplevelSendControlActivated(IntPtr toplevel, WindowControl control) =>
        NativeToplevelSendControlActivated(toplevel, (int)control);

    /// <summary>
    ///     Whether a point is in the window's declared drag region — the title bar,
    ///     usually — and so should move the window rather than reach the client.
    /// </summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="x">Horizontal position within the window.</param>
    /// <param name="y">Vertical position within the window.</param>
    /// <returns><see langword="true" /> if dragging here moves the window.</returns>
    public static bool ToplevelInDragRegion(IntPtr toplevel, int x, int y) =>
        NativeToplevelInDragRegion(toplevel, x, y);

    /// <summary>Tells a client the user chose one of its menu items.</summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="id">The item's client-assigned id.</param>
    public static void MenuSendActivated(IntPtr toplevel, uint id) => NativeMenuSendActivated(toplevel, id);

    /// <summary>
    ///     Tells a client one of its submenus is opening, so it can populate lazily.
    /// </summary>
    /// <param name="toplevel">A window handle.</param>
    /// <param name="id">The submenu's client-assigned id.</param>
    public static void MenuSendAboutToShow(IntPtr toplevel, uint id) => NativeMenuSendAboutToShow(toplevel, id);

    /// <summary>Tells a client nothing of its menu is open any more.</summary>
    /// <param name="toplevel">A window handle.</param>
    public static void MenuSendClosed(IntPtr toplevel) => NativeMenuSendClosed(toplevel);

    /// <summary>
    ///     Reads a NUL-terminated UTF-8 string that C owns.
    /// </summary>
    /// <remarks>
    ///     Public because the unmanaged callbacks receive <c>const char *</c>
    ///     arguments directly and have to turn them into something usable without
    ///     another round trip through this assembly.
    /// </remarks>
    /// <param name="value">A pointer, possibly null.</param>
    /// <returns>The string, or <see langword="null" />.</returns>
    public static string? ReadString(byte* value) => value is null ? null : Marshal.PtrToStringUTF8((IntPtr)value);

    // --- the declarations themselves --------------------------------------

    [LibraryImport(Library, EntryPoint = "trinix_wlr_create")]
    private static partial IntPtr NativeCreate(Callbacks* callbacks, int logLevel);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_start", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool NativeStart(IntPtr server, string? socketName);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_socket")]
    private static partial byte* NativeSocket(IntPtr server);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_run")]
    private static partial void NativeRun(IntPtr server);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_terminate")]
    private static partial void NativeTerminate(IntPtr server);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_destroy")]
    private static partial void NativeDestroy(IntPtr server);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_output_size")]
    private static partial void NativeOutputSize(IntPtr output, out int width, out int height);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_title")]
    private static partial byte* NativeToplevelTitle(IntPtr toplevel);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_app_id")]
    private static partial byte* NativeToplevelAppId(IntPtr toplevel);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_set_position")]
    private static partial void NativeToplevelSetPosition(IntPtr toplevel, int x, int y);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_set_size")]
    private static partial void NativeToplevelSetSize(IntPtr toplevel, int width, int height);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_get_box")]
    private static partial void NativeToplevelGetBox(
        IntPtr toplevel,
        out int x,
        out int y,
        out int width,
        out int height
    );

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_focus")]
    private static partial void NativeToplevelFocus(IntPtr toplevel);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_close")]
    private static partial void NativeToplevelClose(IntPtr toplevel);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_cursor_position")]
    private static partial void NativeCursorPosition(IntPtr server, out double x, out double y);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_at")]
    private static partial IntPtr NativeToplevelAt(IntPtr server, double x, double y);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_pointer_passthrough")]
    private static partial void NativePointerPassthrough(IntPtr server, uint timeMsec);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_control_at")]
    private static partial int NativeToplevelControlAt(IntPtr toplevel, int x, int y);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_send_control_hover")]
    private static partial void NativeToplevelSendControlHover(
        IntPtr toplevel,
        int control,
        uint state
    );

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_send_control_activated")]
    private static partial void NativeToplevelSendControlActivated(IntPtr toplevel, int control);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_toplevel_in_drag_region")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool NativeToplevelInDragRegion(IntPtr toplevel, int x, int y);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_menu_send_activated")]
    private static partial void NativeMenuSendActivated(IntPtr toplevel, uint id);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_menu_send_about_to_show")]
    private static partial void NativeMenuSendAboutToShow(IntPtr toplevel, uint id);

    [LibraryImport(Library, EntryPoint = "trinix_wlr_menu_send_closed")]
    private static partial void NativeMenuSendClosed(IntPtr toplevel);

    /// <summary>Verbosity passed to <see cref="Create" />.</summary>
    public enum LogLevel {
        /// <summary>No output at all.</summary>
        Silent = 0,

        /// <summary>Only failures.</summary>
        Error = 1,

        /// <summary>Failures and lifecycle events.</summary>
        Info = 2,

        /// <summary>Everything, including per-frame detail.</summary>
        Debug = 3
    }

    /// <summary>Input device classes, matching wlroots' own enumeration.</summary>
    public enum InputDeviceType {
        /// <summary>A keyboard.</summary>
        Keyboard = 0,

        /// <summary>A mouse, trackpad or other relative pointing device.</summary>
        Pointing = 1,

        /// <summary>A touchscreen.</summary>
        Touch = 2,

        /// <summary>A drawing tablet.</summary>
        Tablet = 3,

        /// <summary>Anything else the backend chose to expose.</summary>
        Other = 4
    }

    /// <summary>Modifier bits as wlroots reports them.</summary>
    [Flags]
    public enum Modifiers : uint {
        /// <summary>No modifier held.</summary>
        None = 0,

        /// <summary>Shift.</summary>
        Shift = 1 << 0,

        /// <summary>Caps Lock.</summary>
        Caps = 1 << 1,

        /// <summary>Control.</summary>
        Ctrl = 1 << 2,

        /// <summary>Alt.</summary>
        Alt = 1 << 3,

        /// <summary>The Super/Command key.</summary>
        Logo = 1 << 6
    }

    /// <summary>
    ///     The compositor's event handlers, as unmanaged function pointers.
    /// </summary>
    /// <remarks>
    ///     Field order is the contract: this is passed to C as a struct and matched
    ///     positionally against <c>struct trinix_wlr_callbacks</c>. A null entry
    ///     means "not interested", except <see cref="Key" />, where null forwards
    ///     every key to the focused client.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks {
        /// <summary>An output appeared: handle, width, height, refresh in mHz, name.</summary>
        public delegate* unmanaged<IntPtr, int, int, int, byte*, void> OutputAdded;

        /// <summary>An output went away. The handle is invalid after this returns.</summary>
        public delegate* unmanaged<IntPtr, void> OutputRemoved;

        /// <summary>A client created a window. It is not on screen yet.</summary>
        public delegate* unmanaged<IntPtr, void> ToplevelAdded;

        /// <summary>A window became visible and wants a position.</summary>
        public delegate* unmanaged<IntPtr, void> ToplevelMapped;

        /// <summary>A window hid itself without being destroyed.</summary>
        public delegate* unmanaged<IntPtr, void> ToplevelUnmapped;

        /// <summary>A window is gone. The handle is invalid after this returns.</summary>
        public delegate* unmanaged<IntPtr, void> ToplevelRemoved;

        /// <summary>A client asked to be dragged.</summary>
        public delegate* unmanaged<IntPtr, void> ToplevelRequestMove;

        /// <summary>A client asked to be resized from the given edges.</summary>
        public delegate* unmanaged<IntPtr, uint, void> ToplevelRequestResize;

        /// <summary>
        ///     A key was pressed or released: keysym, modifier mask, pressed. Return
        ///     non-zero to consume it instead of forwarding it to the client.
        /// </summary>
        public delegate* unmanaged<uint, uint, byte, byte> Key;

        /// <summary>The pointer moved to these layout coordinates.</summary>
        public delegate* unmanaged<double, double, uint, void> PointerMotion;

        /// <summary>A pointer button changed state.</summary>
        public delegate* unmanaged<uint, byte, uint, void> PointerButton;

        /// <summary>An input device was added: type and name.</summary>
        public delegate* unmanaged<int, byte*, void> InputAdded;

        /// <summary>
        ///     A client declared how it wants to be decorated: window, shadow style,
        ///     corner radius. From <c>trinix-shell-v1</c>.
        /// </summary>
        public delegate* unmanaged<IntPtr, uint, int, void> ToplevelDecorated;

        /// <summary>
        ///     A window control's hit zone moved: window, control, x, y, width,
        ///     height. A zero-sized zone means the control was withdrawn.
        /// </summary>
        public delegate* unmanaged<IntPtr, uint, int, int, int, int, void> ToplevelControl;

        /// <summary>A menu is about to be delivered; discard what was held for this window.</summary>
        public delegate* unmanaged<IntPtr, void> MenuBegin;

        /// <summary>
        ///     One menu item, in tree order: window, id, parent, kind, state,
        ///     keysym, modifiers, label. Parents always arrive before their
        ///     children, so a receiver can build its tree in a single pass.
        /// </summary>
        public delegate* unmanaged<IntPtr, uint, uint, uint, uint, uint, uint, byte*, void> MenuItem;

        /// <summary>The menu is complete: window and the number of items delivered.</summary>
        public delegate* unmanaged<IntPtr, uint, void> MenuEnd;

        /// <summary>The window withdrew its menu bar.</summary>
        public delegate* unmanaged<IntPtr, void> MenuRemoved;
    }

    /// <summary>A window control, from <c>trinix-shell-v1</c>.</summary>
    public enum WindowControl {
        /// <summary>Dismiss the window.</summary>
        Close = 0,

        /// <summary>Hide the window to the dock.</summary>
        Minimise = 1,

        /// <summary>Toggle the window's ideal size.</summary>
        Zoom = 2,

        /// <summary>The pointer is over no control.</summary>
        None = -1
    }

    /// <summary>What the pointer is doing to a window control.</summary>
    public enum ControlHover {
        /// <summary>The pointer is no longer over any control.</summary>
        Left = 0,

        /// <summary>The pointer is over the named control.</summary>
        Entered = 1,

        /// <summary>The named control is being held down.</summary>
        Pressed = 2
    }

    /// <summary>What kind of thing a menu item is, from <c>trinix-menu-v1</c>.</summary>
    public enum MenuItemKind {
        /// <summary>An ordinary item.</summary>
        Item = 0,

        /// <summary>A divider.</summary>
        Separator = 1,

        /// <summary>A container for further items.</summary>
        Submenu = 2,

        /// <summary>An item with an independent on/off state.</summary>
        Checkbox = 3,

        /// <summary>One of a mutually exclusive set.</summary>
        Radio = 4
    }

    /// <summary>Menu item state bits.</summary>
    [Flags]
    public enum MenuItemState : uint {
        /// <summary>Not selectable and not ticked.</summary>
        None = 0,

        /// <summary>Selectable.</summary>
        Enabled = 1,

        /// <summary>Ticked.</summary>
        Checked = 2,

        /// <summary>In the model but not shown.</summary>
        Hidden = 4
    }
}

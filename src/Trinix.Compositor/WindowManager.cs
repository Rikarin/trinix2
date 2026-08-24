using Trinix.Interop;

namespace Trinix.Compositor;

/// <summary>
///     Every decision the compositor makes: where a window opens, what has focus,
///     what a key combination does, and what dragging the pointer means.
/// </summary>
/// <remarks>
///     <para>
///         The counterpart to <c>libtrinix-wlr</c>, which implements the protocols and
///         owns the wlroots objects but decides nothing. If a rule about window
///         behaviour is not in this file, Trinix does not have that rule yet.
///     </para>
///     <para>
///         Not thread-safe, and deliberately: every method runs on the Wayland event
///         loop's single thread. A lock here would only make a mistake elsewhere
///         quieter.
///     </para>
/// </remarks>
sealed class WindowManager {
    // Keysyms, from xkbcommon's keysymdef. Spelled out rather than bound,
    // because binding two constants is more machinery than repeating them.
    const uint KeyEscape = 0xff1b;
    const uint KeyF1 = 0xffbe;
    const uint KeyQ = 0x0071;

    /// <summary>BTN_LEFT, from the kernel's input event codes.</summary>
    const uint ButtonLeft = 0x110;

    /// <summary>
    ///     Where the first window opens, and how far each subsequent one steps.
    ///     A cascade rather than a grid: it is the arrangement that makes it
    ///     obvious at a glance that several windows exist, which is the entire
    ///     requirement until there is a real window management policy.
    /// </summary>
    const int CascadeOrigin = 48;

    const int CascadeStep = 36;
    const int CascadeWrapAfter = 6;

    readonly List<IntPtr> _windows = [];
    readonly Dictionary<IntPtr, MenuBar> _menus = [];
    readonly IntPtr _server;

    PointerMode _mode = PointerMode.Passthrough;
    IntPtr _grabbed;
    double _grabX;
    double _grabY;
    int _grabLeft;
    int _grabTop;
    int _grabRight;
    int _grabBottom;
    Edges _resizeEdges;
    int _cascade;
    IntPtr _hoveredWindow;
    Wlroots.WindowControl _hoveredControl = Wlroots.WindowControl.None;

    /// <summary>The window with keyboard focus, or zero.</summary>
    IntPtr Focused => _windows.Count == 0 ? IntPtr.Zero : _windows[^1];

    /// <summary>Creates a manager for an already-created server.</summary>
    /// <param name="server">A handle from <see cref="Wlroots.Create" />.</param>
    internal WindowManager(IntPtr server) {
        _server = server;
    }

    /// <summary>
    ///     Tracks which window control the pointer is over, telling the clients
    ///     concerned when that changes.
    /// </summary>
    /// <returns><see langword="true" /> if the pointer is over a control.</returns>
    bool UpdateControlHover(double x, double y) {
        var window = Wlroots.ToplevelAt(_server, x, y);
        var control = Wlroots.WindowControl.None;

        if (window != IntPtr.Zero) {
            Wlroots.ToplevelGetBox(window, out var left, out var top, out _, out _);
            control = Wlroots.ToplevelControlAt(window, (int)x - left, (int)y - top);
        }

        if (window == _hoveredWindow && control == _hoveredControl) {
            return control != Wlroots.WindowControl.None;
        }

        // The window being left is told first, so a client never sees itself
        // entered and left in the wrong order when the pointer crosses
        // directly from one window's controls to another's.
        if (_hoveredWindow != IntPtr.Zero
            && _hoveredWindow != window
            && _hoveredControl != Wlroots.WindowControl.None) {
            Wlroots.ToplevelSendControlHover(
                _hoveredWindow,
                Wlroots.WindowControl.None,
                Wlroots.ControlHover.Left
            );
        }

        if (window != IntPtr.Zero) {
            Wlroots.ToplevelSendControlHover(
                window,
                control,
                control == Wlroots.WindowControl.None
                    ? Wlroots.ControlHover.Left
                    : Wlroots.ControlHover.Entered
            );
        }

        _hoveredWindow = window;
        _hoveredControl = control;
        return control != Wlroots.WindowControl.None;
    }

    void BeginGrab(IntPtr window, PointerMode mode, Edges edges) {
        if (!_windows.Contains(window)) {
            return;
        }

        Wlroots.CursorPosition(_server, out var cursorX, out var cursorY);
        Wlroots.ToplevelGetBox(window, out var x, out var y, out var width, out var height);

        _grabbed = window;
        _mode = mode;
        _resizeEdges = edges;

        if (mode == PointerMode.Move) {
            // The offset within the window, so it does not jump to the cursor.
            _grabX = cursorX - x;
            _grabY = cursorY - y;
            return;
        }

        _grabLeft = x;
        _grabTop = y;
        _grabRight = x + width;
        _grabBottom = y + height;

        // For a resize the grab point is the edge being dragged, not the
        // window's corner, so that the edge tracks the pointer exactly.
        _grabX = cursorX - (edges.HasFlag(Edges.Right) ? _grabRight : _grabLeft);
        _grabY = cursorY - (edges.HasFlag(Edges.Bottom) ? _grabBottom : _grabTop);
    }

    void Resize(double cursorX, double cursorY) {
        var borderX = cursorX - _grabX;
        var borderY = cursorY - _grabY;

        var left = _grabLeft;
        var top = _grabTop;
        var right = _grabRight;
        var bottom = _grabBottom;

        if (_resizeEdges.HasFlag(Edges.Top)) {
            top = Math.Min((int)borderY, bottom - 1);
        } else if (_resizeEdges.HasFlag(Edges.Bottom)) {
            bottom = Math.Max((int)borderY, top + 1);
        }

        if (_resizeEdges.HasFlag(Edges.Left)) {
            left = Math.Min((int)borderX, right - 1);
        } else if (_resizeEdges.HasFlag(Edges.Right)) {
            right = Math.Max((int)borderX, left + 1);
        }

        // The move is applied immediately and the size is only requested: a
        // client answers a configure when it is ready, and dragging the left
        // edge would otherwise stretch the window rather than move it.
        Wlroots.ToplevelSetPosition(_grabbed, left, top);
        Wlroots.ToplevelSetSize(_grabbed, right - left, bottom - top);
    }

    void ResetPointer() {
        _mode = PointerMode.Passthrough;
        _grabbed = IntPtr.Zero;
        _resizeEdges = Edges.None;
    }

    void CycleFocus() {
        if (_windows.Count < 2) {
            return;
        }

        // The stack is ordered oldest-first, so the bottom window is the one
        // that has waited longest.
        Raise(_windows[0]);
    }

    void Raise(IntPtr window) {
        if (!_windows.Remove(window)) {
            return;
        }

        _windows.Add(window);
        Wlroots.ToplevelFocus(window);
    }

    void FocusTop() {
        if (Focused != IntPtr.Zero) {
            Wlroots.ToplevelFocus(Focused);
        }
    }

    static string Title(IntPtr window) => Wlroots.ToplevelTitle(window) ?? "untitled";

    /// <summary>Names a trinix-shell-v1 shadow style for the journal.</summary>
    static string ShadowName(uint style) =>
        style switch {
            0 => "none",
            1 => "docked",
            2 => "window",
            3 => "floating",
            _ => $"unknown({style})"
        };

    /// <summary>Records a new output and reports what it can display.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="refreshMilliHertz">Refresh rate in mHz, or zero if the backend has no modes.</param>
    /// <param name="name">The connector's name, such as <c>Virtual-1</c>.</param>
    internal static void OutputAdded(int width, int height, int refreshMilliHertz, string? name) {
        // Printed in a shape a boot check can match on, because for the whole
        // of Phase 4 the only way to see the screen is to read about it.
        Log.Line($"output {name ?? "?"} {width}x{height}@{refreshMilliHertz / 1000.0:0.##}Hz");
    }

    // --- trinix-shell-v1 --------------------------------------------------

    /// <summary>Records how a window asked to be decorated.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="shadowStyle">none, docked, window or floating.</param>
    /// <param name="cornerRadius">Corner radius in surface-local pixels.</param>
    internal static void WindowDecorated(IntPtr window, uint shadowStyle, int cornerRadius) =>
        Log.Line($"decorated '{Title(window)}' shadow={ShadowName(shadowStyle)} radius={cornerRadius}");

    /// <summary>Reports a window control's hit zone moving or being withdrawn.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="control">Which control.</param>
    /// <param name="x">Left edge, window-local.</param>
    /// <param name="y">Top edge, window-local.</param>
    /// <param name="width">Zone width, or zero if withdrawn.</param>
    /// <param name="height">Zone height, or zero if withdrawn.</param>
    internal static void WindowControlZone(
        IntPtr window,
        uint control,
        int x,
        int y,
        int width,
        int height
    ) {
        var named = (Wlroots.WindowControl)control;
        Log.Line(
            width > 0 && height > 0
                ? $"control {named} at {x},{y} {width}x{height} on '{Title(window)}'"
                : $"control {named} withdrawn on '{Title(window)}'"
        );
    }

    /// <summary>Reports a newly attached input device.</summary>
    /// <param name="type">The device class.</param>
    /// <param name="name">The device's own name.</param>
    internal static void InputAdded(Wlroots.InputDeviceType type, string? name) =>
        Log.Line($"input {type.ToString().ToLowerInvariant()} '{name ?? "?"}'");

    /// <summary>What the pointer is currently doing.</summary>
    enum PointerMode {
        /// <summary>Motion belongs to whatever is under the cursor.</summary>
        Passthrough,

        /// <summary>The compositor is dragging a window.</summary>
        Move,

        /// <summary>The compositor is resizing a window.</summary>
        Resize
    }

    /// <summary>xdg-shell's edge bits, from the protocol.</summary>
    [Flags]
    enum Edges : uint {
        None = 0,
        Top = 1,
        Bottom = 2,
        Left = 4,
        Right = 8
    }

    /// <summary>Raised when a keybinding asks the compositor to exit.</summary>
    internal event Action? ExitRequested;

    /// <summary>Places, stacks and focuses a newly visible window.</summary>
    /// <param name="window">The window handle.</param>
    internal void WindowMapped(IntPtr window) {
        _windows.Add(window);

        var step = _cascade % CascadeWrapAfter;
        _cascade++;
        Wlroots.ToplevelSetPosition(
            window,
            CascadeOrigin + step * CascadeStep,
            CascadeOrigin + step * CascadeStep
        );

        Wlroots.ToplevelFocus(window);

        Wlroots.ToplevelGetBox(window, out var x, out var y, out var w, out var h);
        Log.Line($"window mapped '{Title(window)}' {w}x{h} at {x},{y}");
    }

    /// <summary>Forgets a window that is no longer on screen.</summary>
    /// <param name="window">The window handle.</param>
    internal void WindowUnmapped(IntPtr window) {
        if (!_windows.Remove(window)) {
            return;
        }

        // A window that vanishes mid-drag would otherwise leave the pointer
        // grabbed by nothing.
        if (_grabbed == window) {
            ResetPointer();
        }

        Log.Line($"window unmapped '{Title(window)}'");
        FocusTop();
    }

    /// <summary>Drops a destroyed window, in case it was never unmapped.</summary>
    /// <param name="window">The window handle, no longer valid.</param>
    internal void WindowRemoved(IntPtr window) {
        _menus.Remove(window);
        if (_hoveredWindow == window) {
            _hoveredWindow = IntPtr.Zero;
            _hoveredControl = Wlroots.WindowControl.None;
        }

        if (_windows.Remove(window) && _grabbed == window) {
            ResetPointer();
        }
    }

    // --- trinix-menu-v1 ---------------------------------------------------

    /// <summary>A window is about to deliver a new menu model.</summary>
    /// <param name="window">The window handle.</param>
    internal void MenuBegin(IntPtr window) {
        if (!_menus.TryGetValue(window, out var bar)) {
            bar = new();
            _menus[window] = bar;
        }

        bar.Begin();
    }

    /// <summary>One item of the model being delivered.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="entry">The item.</param>
    internal void MenuItem(IntPtr window, MenuEntry entry) {
        if (_menus.TryGetValue(window, out var bar)) {
            bar.Add(entry);
        }
    }

    /// <summary>The model is complete and becomes the window's menu bar.</summary>
    /// <param name="window">The window handle.</param>
    internal void MenuEnd(IntPtr window) {
        if (!_menus.TryGetValue(window, out var bar)) {
            return;
        }

        bar.End();

        // Printed rather than drawn, and that is where Phase 5 currently
        // stands: the model arrives complete and the shell holds it, but the
        // image has no font, so there is nothing to render the words with. The
        // line below is what the boot check reads instead.
        Log.Line($"menu bar for '{Title(window)}': {bar.Describe()}");
    }

    /// <summary>A window withdrew its menu bar.</summary>
    /// <param name="window">The window handle.</param>
    internal void MenuRemoved(IntPtr window) {
        if (_menus.Remove(window)) {
            Log.Line($"menu bar withdrawn by '{Title(window)}'");
        }
    }

    /// <summary>Begins dragging a window at the client's request.</summary>
    /// <param name="window">The window handle.</param>
    internal void RequestMove(IntPtr window) => BeginGrab(window, PointerMode.Move, Edges.None);

    /// <summary>Begins resizing a window at the client's request.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="edges">The edges the client wants moved.</param>
    internal void RequestResize(IntPtr window, uint edges) => BeginGrab(window, PointerMode.Resize, (Edges)edges);

    /// <summary>
    ///     Offers a key to the compositor's own bindings.
    /// </summary>
    /// <param name="keysym">The xkbcommon keysym.</param>
    /// <param name="modifiers">Modifiers held at the time.</param>
    /// <param name="pressed"><see langword="true" /> for a press.</param>
    /// <returns><see langword="true" /> if the compositor consumed the key.</returns>
    internal bool Key(uint keysym, Wlroots.Modifiers modifiers, bool pressed) {
        // Menu accelerators first, and this ordering is the promise the menu
        // protocol makes: a shortcut on a menu item works whether or not the
        // menu has ever been opened, so the shell has to claim the key before
        // the focused client sees it.
        if (Focused != IntPtr.Zero && _menus.TryGetValue(Focused, out var bar)) {
            var item = bar.FindAccelerator(keysym, modifiers);
            if (item != 0) {
                if (pressed) {
                    Log.Line($"accelerator activated menu item {item}");
                    Wlroots.MenuSendActivated(Focused, item);
                }

                return true;
            }
        }

        // Bindings fire on press and swallow the matching release, so a client
        // never sees half of one.
        if (!modifiers.HasFlag(Wlroots.Modifiers.Alt)) {
            return false;
        }

        switch (keysym) {
            case KeyEscape:
                if (pressed) {
                    Log.Line("exit requested from the keyboard");
                    ExitRequested?.Invoke();
                }

                return true;

            case KeyF1:
                if (pressed) {
                    CycleFocus();
                }

                return true;

            case KeyQ:
                if (pressed && Focused != IntPtr.Zero) {
                    Wlroots.ToplevelClose(Focused);
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>Acts on pointer motion according to the current mode.</summary>
    /// <param name="x">Pointer position in layout coordinates.</param>
    /// <param name="y">Pointer position in layout coordinates.</param>
    /// <param name="timeMsec">Event timestamp.</param>
    internal void PointerMotion(double x, double y, uint timeMsec) {
        switch (_mode) {
            case PointerMode.Move:
                Wlroots.ToplevelSetPosition(_grabbed, (int)(x - _grabX), (int)(y - _grabY));
                break;

            case PointerMode.Resize:
                Resize(x, y);
                break;

            default:
                // Window controls are hit-tested before anything is forwarded,
                // because a pointer over a traffic light is the compositor's
                // business and not the application's. That is what lets the
                // glyphs light up while the client is blocked.
                if (UpdateControlHover(x, y)) {
                    return;
                }

                Wlroots.PointerPassthrough(_server, timeMsec);
                break;
        }
    }

    /// <summary>Handles focus-follows-click, and the end of a drag.</summary>
    /// <param name="button">The kernel button code.</param>
    /// <param name="pressed"><see langword="true" /> for a press.</param>
    internal void PointerButton(uint button, bool pressed) {
        if (!pressed) {
            ResetPointer();
            return;
        }

        if (button != ButtonLeft) {
            return;
        }

        Wlroots.CursorPosition(_server, out var x, out var y);
        var window = Wlroots.ToplevelAt(_server, x, y);
        if (window == IntPtr.Zero) {
            return;
        }

        Raise(window);

        Wlroots.ToplevelGetBox(window, out var left, out var top, out _, out _);
        var localX = (int)x - left;
        var localY = (int)y - top;

        // A traffic light is not a click the application ever hears about — it
        // hears the *outcome*, which is a request it may refuse. Closing a
        // window with unsaved work has to remain the application's decision.
        var control = Wlroots.ToplevelControlAt(window, localX, localY);
        if (control != Wlroots.WindowControl.None) {
            Log.Line($"control {control} activated on '{Title(window)}'");
            Wlroots.ToplevelSendControlActivated(window, control);
            return;
        }

        // A press in the title bar starts a compositor-driven drag. The client
        // never sees the motion, which is the entire reason the region was
        // declared: a window whose application is busy still moves.
        if (Wlroots.ToplevelInDragRegion(window, localX, localY)) {
            BeginGrab(window, PointerMode.Move, Edges.None);
        }
    }
}

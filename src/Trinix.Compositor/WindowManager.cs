using Trinix.Interop;

namespace Trinix.Compositor;

/// <summary>
/// Every decision the compositor makes: where a window opens, what has focus,
/// what a key combination does, and what dragging the pointer means.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>libtrinix-wlr</c>, which implements the protocols and
/// owns the wlroots objects but decides nothing. If a rule about window
/// behaviour is not in this file, Trinix does not have that rule yet.
/// </para>
/// <para>
/// Not thread-safe, and deliberately: every method runs on the Wayland event
/// loop's single thread. A lock here would only make a mistake elsewhere
/// quieter.
/// </para>
/// </remarks>
internal sealed class WindowManager
{
    /// <summary>What the pointer is currently doing.</summary>
    private enum PointerMode
    {
        /// <summary>Motion belongs to whatever is under the cursor.</summary>
        Passthrough,

        /// <summary>The compositor is dragging a window.</summary>
        Move,

        /// <summary>The compositor is resizing a window.</summary>
        Resize,
    }

    /// <summary>xdg-shell's edge bits, from the protocol.</summary>
    [Flags]
    private enum Edges : uint
    {
        None = 0,
        Top = 1,
        Bottom = 2,
        Left = 4,
        Right = 8,
    }

    // Keysyms, from xkbcommon's keysymdef. Spelled out rather than bound,
    // because binding two constants is more machinery than repeating them.
    private const uint KeyEscape = 0xff1b;
    private const uint KeyF1 = 0xffbe;
    private const uint KeyQ = 0x0071;

    /// <summary>BTN_LEFT, from the kernel's input event codes.</summary>
    private const uint ButtonLeft = 0x110;

    /// <summary>
    /// Where the first window opens, and how far each subsequent one steps.
    /// A cascade rather than a grid: it is the arrangement that makes it
    /// obvious at a glance that several windows exist, which is the entire
    /// requirement until there is a real window management policy.
    /// </summary>
    private const int CascadeOrigin = 48;
    private const int CascadeStep = 36;
    private const int CascadeWrapAfter = 6;

    private readonly List<IntPtr> _windows = [];
    private readonly IntPtr _server;

    private PointerMode _mode = PointerMode.Passthrough;
    private IntPtr _grabbed;
    private double _grabX;
    private double _grabY;
    private int _grabLeft;
    private int _grabTop;
    private int _grabRight;
    private int _grabBottom;
    private Edges _resizeEdges;
    private int _cascade;

    /// <summary>Creates a manager for an already-created server.</summary>
    /// <param name="server">A handle from <see cref="Wlroots.Create"/>.</param>
    internal WindowManager(IntPtr server) => _server = server;

    /// <summary>Raised when a keybinding asks the compositor to exit.</summary>
    internal event Action? ExitRequested;

    /// <summary>The window with keyboard focus, or zero.</summary>
    private IntPtr Focused => _windows.Count == 0 ? IntPtr.Zero : _windows[^1];

    /// <summary>Records a new output and reports what it can display.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="refreshMilliHertz">Refresh rate in mHz, or zero if the backend has no modes.</param>
    /// <param name="name">The connector's name, such as <c>Virtual-1</c>.</param>
    internal static void OutputAdded(int width, int height, int refreshMilliHertz, string? name)
    {
        // Printed in a shape a boot check can match on, because for the whole
        // of Phase 4 the only way to see the screen is to read about it.
        Log.Line($"output {name ?? "?"} {width}x{height}@{refreshMilliHertz / 1000.0:0.##}Hz");
    }

    /// <summary>Places, stacks and focuses a newly visible window.</summary>
    /// <param name="window">The window handle.</param>
    internal void WindowMapped(IntPtr window)
    {
        _windows.Add(window);

        int step = _cascade % CascadeWrapAfter;
        _cascade++;
        Wlroots.ToplevelSetPosition(window,
            CascadeOrigin + (step * CascadeStep),
            CascadeOrigin + (step * CascadeStep));

        Wlroots.ToplevelFocus(window);

        Wlroots.ToplevelGetBox(window, out int x, out int y, out int w, out int h);
        Log.Line($"window mapped '{Title(window)}' {w}x{h} at {x},{y}");
    }

    /// <summary>Forgets a window that is no longer on screen.</summary>
    /// <param name="window">The window handle.</param>
    internal void WindowUnmapped(IntPtr window)
    {
        if (!_windows.Remove(window))
        {
            return;
        }

        // A window that vanishes mid-drag would otherwise leave the pointer
        // grabbed by nothing.
        if (_grabbed == window)
        {
            ResetPointer();
        }

        Log.Line($"window unmapped '{Title(window)}'");
        FocusTop();
    }

    /// <summary>Drops a destroyed window, in case it was never unmapped.</summary>
    /// <param name="window">The window handle, no longer valid.</param>
    internal void WindowRemoved(IntPtr window)
    {
        if (_windows.Remove(window) && _grabbed == window)
        {
            ResetPointer();
        }
    }

    /// <summary>Begins dragging a window at the client's request.</summary>
    /// <param name="window">The window handle.</param>
    internal void RequestMove(IntPtr window) => BeginGrab(window, PointerMode.Move, Edges.None);

    /// <summary>Begins resizing a window at the client's request.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="edges">The edges the client wants moved.</param>
    internal void RequestResize(IntPtr window, uint edges) =>
        BeginGrab(window, PointerMode.Resize, (Edges)edges);

    /// <summary>
    /// Offers a key to the compositor's own bindings.
    /// </summary>
    /// <param name="keysym">The xkbcommon keysym.</param>
    /// <param name="modifiers">Modifiers held at the time.</param>
    /// <param name="pressed"><see langword="true"/> for a press.</param>
    /// <returns><see langword="true"/> if the compositor consumed the key.</returns>
    internal bool Key(uint keysym, Wlroots.Modifiers modifiers, bool pressed)
    {
        // Bindings fire on press and swallow the matching release, so a client
        // never sees half of one.
        if (!modifiers.HasFlag(Wlroots.Modifiers.Alt))
        {
            return false;
        }

        switch (keysym)
        {
            case KeyEscape:
                if (pressed)
                {
                    Log.Line("exit requested from the keyboard");
                    ExitRequested?.Invoke();
                }
                return true;

            case KeyF1:
                if (pressed)
                {
                    CycleFocus();
                }
                return true;

            case KeyQ:
                if (pressed && Focused != IntPtr.Zero)
                {
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
    internal void PointerMotion(double x, double y, uint timeMsec)
    {
        switch (_mode)
        {
            case PointerMode.Move:
                Wlroots.ToplevelSetPosition(_grabbed, (int)(x - _grabX), (int)(y - _grabY));
                break;

            case PointerMode.Resize:
                Resize(x, y);
                break;

            default:
                Wlroots.PointerPassthrough(_server, timeMsec);
                break;
        }
    }

    /// <summary>Handles focus-follows-click, and the end of a drag.</summary>
    /// <param name="button">The kernel button code.</param>
    /// <param name="pressed"><see langword="true"/> for a press.</param>
    internal void PointerButton(uint button, bool pressed)
    {
        if (!pressed)
        {
            ResetPointer();
            return;
        }

        if (button != ButtonLeft)
        {
            return;
        }

        Wlroots.CursorPosition(_server, out double x, out double y);
        IntPtr window = Wlroots.ToplevelAt(_server, x, y);
        if (window != IntPtr.Zero)
        {
            Raise(window);
        }
    }

    /// <summary>Reports a newly attached input device.</summary>
    /// <param name="type">The device class.</param>
    /// <param name="name">The device's own name.</param>
    internal static void InputAdded(Wlroots.InputDeviceType type, string? name) =>
        Log.Line($"input {type.ToString().ToLowerInvariant()} '{name ?? "?"}'");

    private void BeginGrab(IntPtr window, PointerMode mode, Edges edges)
    {
        if (!_windows.Contains(window))
        {
            return;
        }

        Wlroots.CursorPosition(_server, out double cursorX, out double cursorY);
        Wlroots.ToplevelGetBox(window, out int x, out int y, out int width, out int height);

        _grabbed = window;
        _mode = mode;
        _resizeEdges = edges;

        if (mode == PointerMode.Move)
        {
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

    private void Resize(double cursorX, double cursorY)
    {
        double borderX = cursorX - _grabX;
        double borderY = cursorY - _grabY;

        int left = _grabLeft;
        int top = _grabTop;
        int right = _grabRight;
        int bottom = _grabBottom;

        if (_resizeEdges.HasFlag(Edges.Top))
        {
            top = Math.Min((int)borderY, bottom - 1);
        }
        else if (_resizeEdges.HasFlag(Edges.Bottom))
        {
            bottom = Math.Max((int)borderY, top + 1);
        }

        if (_resizeEdges.HasFlag(Edges.Left))
        {
            left = Math.Min((int)borderX, right - 1);
        }
        else if (_resizeEdges.HasFlag(Edges.Right))
        {
            right = Math.Max((int)borderX, left + 1);
        }

        // The move is applied immediately and the size is only requested: a
        // client answers a configure when it is ready, and dragging the left
        // edge would otherwise stretch the window rather than move it.
        Wlroots.ToplevelSetPosition(_grabbed, left, top);
        Wlroots.ToplevelSetSize(_grabbed, right - left, bottom - top);
    }

    private void ResetPointer()
    {
        _mode = PointerMode.Passthrough;
        _grabbed = IntPtr.Zero;
        _resizeEdges = Edges.None;
    }

    private void CycleFocus()
    {
        if (_windows.Count < 2)
        {
            return;
        }

        // The stack is ordered oldest-first, so the bottom window is the one
        // that has waited longest.
        Raise(_windows[0]);
    }

    private void Raise(IntPtr window)
    {
        if (!_windows.Remove(window))
        {
            return;
        }

        _windows.Add(window);
        Wlroots.ToplevelFocus(window);
    }

    private void FocusTop()
    {
        if (Focused != IntPtr.Zero)
        {
            Wlroots.ToplevelFocus(Focused);
        }
    }

    private static string Title(IntPtr window) => Wlroots.ToplevelTitle(window) ?? "untitled";
}

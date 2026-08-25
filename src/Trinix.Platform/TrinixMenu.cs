using Trinix.Interop;
using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     An application's menu bar, in the bar at the top of the screen.
/// </summary>
/// <remarks>
///     <para>
///         The mac-like half of the platform, and the reason a Trinix window is not
///         just an SDL window with a different backend. Menus live in the bar at the
///         top of the screen rather than inside the window, which means the *shell*
///         draws them from a model this exports — see
///         <c>base/recipes/trinix-protocols/protocol/trinix-menu-v1.xml</c>.
///     </para>
///     <para>
///         <b>The application's, not a window's.</b> <see cref="ForApplication" /> is
///         the entry point, and what it returns covers every window this process
///         opens, does not change as the user moves between them, and goes on
///         existing when they are all closed — which is how File ▸ New is reachable
///         from an application that has nothing on screen. A window that genuinely
///         needs different menus overrides them with <see cref="ForWindow" />, and
///         almost none do.
///     </para>
///     <para>
///         <b>Not part of <see cref="IPlatform" />, deliberately.</b> Vixen's platform
///         contract has no menu concept, because three of its six targets have no menu
///         bar and a fourth puts it in the window. Adding one to the contract would
///         make every other platform implement a thing it cannot have; exposing it
///         here means an application that wants a Trinix menu bar asks Trinix for one,
///         which is honest about what it is doing.
///     </para>
///     <para>
///         Changes accumulate until <see cref="Commit" />, so a menu bar is never
///         drawn half-built.
///     </para>
/// </remarks>
public sealed class TrinixMenu {
    /// <summary>
    ///     Every menu this process has exported, by the native handle for it.
    /// </summary>
    /// <remarks>
    ///     Keyed on the menu because the compositor's events name a menu: there is
    ///     ordinarily one entry, the application's, and one more for each window that
    ///     overrode it. Static because a process has one connection, which is the same
    ///     reason <see cref="TrinixPlatform" /> keeps one.
    /// </remarks>
    static readonly Dictionary<IntPtr, TrinixMenu> s_menus = [];

    readonly IntPtr menu;

    /// <summary>The window this overrides, or zero for the application's own bar.</summary>
    readonly IntPtr window;

    TrinixMenu(IntPtr menu, IntPtr window) {
        this.menu = menu;
        this.window = window;
    }

    /// <summary>The user chose an item. Leaf items only.</summary>
    /// <remarks>
    ///     ⚠ A checkbox is not toggled by being chosen. Whether it should be is the
    ///     application's decision, and it says so with <see cref="Update" /> — which is
    ///     what lets an item refuse, ask first, or change something else instead.
    /// </remarks>
    public event Action<uint>? ItemActivated;

    /// <summary>A submenu is opening, which is where to populate it lazily.</summary>
    public event Action<uint>? SubmenuOpening;

    /// <summary>The menu session ended, however it ended.</summary>
    public event Action? Dismissed;

    /// <summary>Takes over this application's menu bar.</summary>
    /// <remarks>
    ///     <para>
    ///         The ordinary way to get a menu bar, and for nearly every application the
    ///         only one. It is not tied to a window: an application may build its menus
    ///         before opening one, and must keep them after closing the last, because a
    ///         Mac user's way back to a window is the File menu of an application with
    ///         none.
    ///     </para>
    ///     <para>
    ///         Safe to call repeatedly; the same menu comes back each time.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     The menu, or <see langword="null" /> when the compositor offered no
    ///     <c>trinix_menu_manager_v1</c> — which is a system with no menu bar, and an
    ///     application that is otherwise fine.
    /// </returns>
    /// <exception cref="InvalidOperationException">No Trinix platform is connected.</exception>
    public static TrinixMenu? ForApplication() {
        var client = TrinixPlatform.ClientHandle;
        if (client == IntPtr.Zero) {
            throw new InvalidOperationException(
                "There is no Trinix platform connected to export a menu bar to."
            );
        }

        return Adopt(WaylandClient.CreateApplicationMenu(client), IntPtr.Zero);
    }

    /// <summary>Overrides the application's menu bar for one window.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The exception, not the shorthand for <see cref="ForApplication" />. The
    ///         shell shows this in place of the application's bar while this window is
    ///         active — right for a window whose menus genuinely are not the
    ///         application's, such as an inspector or a console, and wrong for every
    ///         other window.
    ///     </para>
    ///     <para>
    ///         Calling it per window recreates what per-window scoping cost in the first
    ///         place: N copies of one tree, and every state change applied N times, with
    ///         the copies free to drift apart. If two windows would get the same menus,
    ///         they want <see cref="ForApplication" />.
    ///     </para>
    ///     <para>
    ///         The override is withdrawn when the window closes, and the application's
    ///         own bar is shown again.
    ///     </para>
    /// </remarks>
    /// <param name="window">The window whose menus differ from the application's.</param>
    /// <returns>
    ///     The menu, or <see langword="null" /> when there is no menu bar on this
    ///     system.
    /// </returns>
    /// <exception cref="ArgumentException">The window is not a Trinix window.</exception>
    public static TrinixMenu? ForWindow(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);

        if (window is not TrinixWindow trinix || trinix.Handle == IntPtr.Zero) {
            throw new ArgumentException("Not a window this platform created.", nameof(window));
        }

        return Adopt(WaylandClient.CreateWindowMenu(trinix.Handle), trinix.Handle);
    }

    /// <summary>Wraps a native menu, or returns the wrapper it already has.</summary>
    /// <remarks>
    ///     Both creation paths are idempotent in the C library, so a second call
    ///     returns the same handle — and must therefore return the same
    ///     <see cref="TrinixMenu" />, or an application would lose the event handlers
    ///     it attached to the first one.
    /// </remarks>
    /// <param name="handle">A menu handle, possibly zero.</param>
    /// <param name="window">The window it overrides, or zero for the application's bar.</param>
    /// <returns>The menu, or <see langword="null" /> for a zero handle.</returns>
    static TrinixMenu? Adopt(IntPtr handle, IntPtr window) {
        if (handle == IntPtr.Zero) {
            return null;
        }

        if (s_menus.TryGetValue(handle, out var existing)) {
            return existing;
        }

        var created = new TrinixMenu(handle, window);
        s_menus[handle] = created;
        return created;
    }

    /// <summary>Adds an item.</summary>
    /// <param name="id">Client-assigned, non-zero, and unique within this menu.</param>
    /// <param name="label">The text, with an underscore before the mnemonic.</param>
    /// <param name="parent">The containing submenu, or zero for a top-level menu.</param>
    /// <param name="index">Where among its siblings; negative appends.</param>
    /// <param name="kind">What the entry is.</param>
    public void Insert(uint id, string label, uint parent = 0, int index = -1,
                       MenuItemKind kind = MenuItemKind.Item) =>
        WaylandClient.InsertMenuItem(menu, id, parent, index, (WaylandClient.MenuKind)kind, label);

    /// <summary>Changes an item's label or state.</summary>
    /// <param name="id">The item.</param>
    /// <param name="label">The new text.</param>
    /// <param name="state">Enabled, checked, hidden.</param>
    /// <remarks>
    ///     ⚠ This is where the focused window reaches the menu, and the only place it
    ///     does. Trinix has no responder chain, so nothing greys <b>Save</b> on the
    ///     application's behalf when focus moves to a window that cannot save — the
    ///     application says so here, on the state change it is already handling.
    /// </remarks>
    public void Update(uint id, string label, MenuItemState state = MenuItemState.Enabled) =>
        WaylandClient.UpdateMenuItem(menu, id, label, (WaylandClient.MenuState)state);

    /// <summary>Gives an item a keyboard shortcut.</summary>
    /// <param name="id">The item.</param>
    /// <param name="keysym">An xkb keysym — <c>XKB_KEY_s</c> is 0x0073.</param>
    /// <param name="modifiers">Which modifiers must be held.</param>
    /// <remarks>
    ///     ⚠ The shell handles it, so the key never reaches the application as a key
    ///     press. That is the point of registering it — and the reason an accelerator
    ///     the application also handles itself will fire twice on every other platform
    ///     and once here.
    /// </remarks>
    public void SetAccelerator(uint id, uint keysym, MenuModifiers modifiers) =>
        WaylandClient.SetMenuAccelerator(menu, id, keysym, (WaylandClient.Modifiers)modifiers);

    /// <summary>Removes an item and everything under it.</summary>
    /// <param name="id">The item.</param>
    public void Remove(uint id) => WaylandClient.RemoveMenuItem(menu, id);

    /// <summary>Applies everything since the last commit.</summary>
    public void Commit() => WaylandClient.CommitMenu(menu);

    /// <summary>Withdraws the menu bar.</summary>
    /// <remarks>
    ///     Rarely what an application wants. An application's menus exist for as long
    ///     as the application does, and a window's override goes away with the window
    ///     without anyone asking.
    /// </remarks>
    public void Destroy() {
        s_menus.Remove(menu);
        WaylandClient.DestroyMenu(menu);
    }

    internal static void Activated(IntPtr handle, uint id) {
        if (s_menus.TryGetValue(handle, out var menu)) {
            menu.ItemActivated?.Invoke(id);
        }
    }

    internal static void AboutToShow(IntPtr handle, uint id) {
        if (s_menus.TryGetValue(handle, out var menu)) {
            menu.SubmenuOpening?.Invoke(id);
        }
    }

    internal static void Closed(IntPtr handle) {
        if (s_menus.TryGetValue(handle, out var menu)) {
            menu.Dismissed?.Invoke();
        }
    }

    /// <summary>
    ///     Drops the override a destroyed window owned, if it had one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not optional bookkeeping. The C library destroys a window's override with
    ///     the window, so the handle held here becomes an address the allocator is free
    ///     to hand back for the next menu — and an entry left behind would answer that
    ///     menu's events with the dead window's handlers. The application's own bar is
    ///     never touched: it belongs to the connection, and the connection is still up.
    /// </remarks>
    /// <param name="window">The window handle, no longer valid.</param>
    internal static void ForgetWindow(IntPtr window) {
        foreach (var (handle, menu) in s_menus) {
            if (menu.window == window) {
                s_menus.Remove(handle);
                return;
            }
        }
    }
}

/// <summary>What a menu entry is.</summary>
public enum MenuItemKind : uint {
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
public enum MenuItemState : uint {
    /// <summary>Shown, not selectable, not ticked.</summary>
    None = 0,

    /// <summary>Selectable.</summary>
    Enabled = 1,

    /// <summary>Ticked.</summary>
    Checked = 2,

    /// <summary>In the model but not shown.</summary>
    Hidden = 4
}

/// <summary>Accelerator modifiers, which are xkb's masks.</summary>
[Flags]
public enum MenuModifiers : uint {
    /// <summary>None held.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control.</summary>
    Control = 4,

    /// <summary>Alt.</summary>
    Alt = 8,

    /// <summary>The Command key.</summary>
    Command = 64
}

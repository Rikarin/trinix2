using Trinix.Interop;
using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     A window's entry in the system menu bar.
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
    static readonly Dictionary<IntPtr, TrinixMenu> s_menus = [];

    readonly IntPtr window;

    TrinixMenu(IntPtr window) => this.window = window;

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

    /// <summary>Takes over a window's menu bar.</summary>
    /// <param name="window">The window whose menus these are.</param>
    /// <returns>
    ///     The menu, or <see langword="null" /> when the compositor offered no
    ///     <c>trinix_menu_manager_v1</c> — which is a system with no menu bar, and an
    ///     application that is otherwise fine.
    /// </returns>
    /// <exception cref="ArgumentException">The window is not a Trinix window.</exception>
    public static TrinixMenu? For(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);

        if (window is not TrinixWindow trinix || trinix.Handle == IntPtr.Zero) {
            throw new ArgumentException("Not a window this platform created.", nameof(window));
        }

        if (s_menus.TryGetValue(trinix.Handle, out var existing)) {
            return existing;
        }

        var menu = new TrinixMenu(trinix.Handle);
        s_menus[trinix.Handle] = menu;
        return menu;
    }

    /// <summary>Adds an item.</summary>
    /// <param name="id">Client-assigned, non-zero, and unique within this menu.</param>
    /// <param name="label">The text, with an underscore before the mnemonic.</param>
    /// <param name="parent">The containing submenu, or zero for a top-level menu.</param>
    /// <param name="index">Where among its siblings; negative appends.</param>
    /// <param name="kind">What the entry is.</param>
    public void Insert(uint id, string label, uint parent = 0, int index = -1,
                       MenuItemKind kind = MenuItemKind.Item) =>
        WaylandClient.InsertMenuItem(window, id, parent, index, (WaylandClient.MenuKind)kind, label);

    /// <summary>Changes an item's label or state.</summary>
    /// <param name="id">The item.</param>
    /// <param name="label">The new text.</param>
    /// <param name="state">Enabled, checked, hidden.</param>
    public void Update(uint id, string label, MenuItemState state = MenuItemState.Enabled) =>
        WaylandClient.UpdateMenuItem(window, id, label, (WaylandClient.MenuState)state);

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
        WaylandClient.SetMenuAccelerator(window, id, keysym, (WaylandClient.Modifiers)modifiers);

    /// <summary>Removes an item and everything under it.</summary>
    /// <param name="id">The item.</param>
    public void Remove(uint id) => WaylandClient.RemoveMenuItem(window, id);

    /// <summary>Applies everything since the last commit.</summary>
    public void Commit() => WaylandClient.CommitMenu(window);

    internal static void Activated(IntPtr window, uint id) {
        if (s_menus.TryGetValue(window, out var menu)) {
            menu.ItemActivated?.Invoke(id);
        }
    }

    internal static void AboutToShow(IntPtr window, uint id) {
        if (s_menus.TryGetValue(window, out var menu)) {
            menu.SubmenuOpening?.Invoke(id);
        }
    }

    internal static void Closed(IntPtr window) {
        if (s_menus.TryGetValue(window, out var menu)) {
            menu.Dismissed?.Invoke();
        }
    }

    internal static void Forget(IntPtr window) => s_menus.Remove(window);
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

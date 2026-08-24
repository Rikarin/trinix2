using Trinix.Interop;

namespace Trinix.Compositor;

/// <summary>One entry in a window's menu bar.</summary>
/// <param name="Id">The client's own identifier for it.</param>
/// <param name="Parent">The containing submenu, or zero for a top-level menu.</param>
/// <param name="Kind">Item, separator, submenu, checkbox or radio.</param>
/// <param name="State">Enabled, checked, hidden.</param>
/// <param name="Keysym">An xkb keysym, or zero for no accelerator.</param>
/// <param name="Modifiers">Modifiers the accelerator needs.</param>
/// <param name="Label">The text, with an underscore before the mnemonic if any.</param>
readonly record struct MenuEntry(
    uint Id,
    uint Parent,
    Wlroots.MenuItemKind Kind,
    Wlroots.MenuItemState State,
    uint Keysym,
    Wlroots.Modifiers Modifiers,
    string Label
);

/// <summary>
///     The menu model a window exported, as the shell holds it.
/// </summary>
/// <remarks>
///     <para>
///         A flat list in tree order rather than a tree of objects, because that is the
///         shape it arrives in and the shape it is drawn in. The compositor emits
///         parents before children, so anything that needs the hierarchy can recover it
///         from <see cref="MenuEntry.Parent" /> in one pass, and the common operations —
///         draw the bar, find an accelerator — are a single walk either way.
///     </para>
///     <para>
///         Replaced wholesale on every commit. That is not a shortcut: the protocol
///         applies changes atomically precisely so that the shell never has to reason
///         about a partially rebuilt menu, and rebuilding a list of a few dozen structs
///         is cheaper than working out what changed.
///     </para>
/// </remarks>
sealed class MenuBar {
    readonly List<MenuEntry> _entries = [];
    List<MenuEntry>? _pending;

    /// <summary>Everything in the bar, parents before children.</summary>
    internal IReadOnlyList<MenuEntry> Entries => _entries;

    /// <summary>The top-level menus, in the order they are shown.</summary>
    internal IEnumerable<MenuEntry> TopLevel => _entries.Where(e => e.Parent == 0);

    /// <summary>Starts receiving a new model. Nothing changes until <see cref="End" />.</summary>
    internal void Begin() => _pending = [];

    /// <summary>Adds one item to the model being received.</summary>
    /// <param name="entry">The item, as the client described it.</param>
    internal void Add(MenuEntry entry) => _pending?.Add(entry);

    /// <summary>Publishes the received model, replacing what was there.</summary>
    internal void End() {
        if (_pending is null) {
            return;
        }

        _entries.Clear();
        _entries.AddRange(_pending);
        _pending = null;
    }

    /// <summary>Forgets the model, for a window that withdrew its menu bar.</summary>
    internal void Clear() {
        _entries.Clear();
        _pending = null;
    }

    /// <summary>
    ///     Finds the item a key combination should trigger.
    /// </summary>
    /// <remarks>
    ///     The shell honours accelerators whether or not the menu has ever been
    ///     opened — which is why the compositor is told about them at all, and why
    ///     this lookup exists on the model rather than on anything that draws.
    ///     Disabled and hidden items are not reachable by their shortcut, for the
    ///     same reason they are not clickable.
    /// </remarks>
    /// <param name="keysym">The xkb keysym that was pressed.</param>
    /// <param name="modifiers">Modifiers held at the time.</param>
    /// <returns>The item's id, or zero if nothing matches.</returns>
    internal uint FindAccelerator(uint keysym, Wlroots.Modifiers modifiers) {
        foreach (var entry in _entries) {
            if (entry.Keysym == 0 || entry.Keysym != keysym || entry.Modifiers != modifiers) {
                continue;
            }

            if (!entry.State.HasFlag(Wlroots.MenuItemState.Enabled)
                || entry.State.HasFlag(Wlroots.MenuItemState.Hidden)) {
                continue;
            }

            return entry.Id;
        }

        return 0;
    }

    /// <summary>A one-line summary of the bar, for the journal.</summary>
    /// <returns>The top-level menu labels and the total item count.</returns>
    internal string Describe() {
        var titles = TopLevel
            .Where(e => !e.State.HasFlag(Wlroots.MenuItemState.Hidden))
            .Select(e => e.Label.Replace("_", string.Empty, StringComparison.Ordinal));

        return $"{_entries.Count} items [{string.Join(", ", titles)}]";
    }
}

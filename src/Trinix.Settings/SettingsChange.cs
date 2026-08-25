using System.Collections.Generic;

namespace Trinix.Settings;

/// <summary>
///     A key, the value it resolved to, and the layer that decided it.
/// </summary>
/// <remarks>
///     ⚠ <paramref name="Layer" /> being <see langword="null" /> means the schema's
///     compiled-in default answered — no layer holds a value for this key at all. Doc 08's
///     panes need the distinction in two places: "reset" is only meaningful when some layer
///     holds a value, and a control whose value came from
///     <see cref="SettingsLayer.ManagedProfile" /> is one the user may not change and should
///     be told why.
/// </remarks>
/// <param name="Key">The key.</param>
/// <param name="Value">What it resolves to now.</param>
/// <param name="Layer">Which layer supplied it, or <see langword="null" /> for the schema default.</param>
public sealed record SettingsResolution(SettingsKey Key, SettingValue Value, SettingsLayer? Layer);

/// <summary>
///     What one commit changed.
/// </summary>
/// <remarks>
///     <para>
///         One event per commit, never one per key: doc 02 asks for changes as events rather
///         than polls, and a multi-key write that raised one event per key would hand every
///         subscriber a torn view of a change that was atomic in the database. A pane that
///         re-reads on this event sees the whole write or none of it.
///     </para>
///     <para>
///         ⚠ <see cref="Changes" /> lists keys whose <i>resolved</i> value moved, which is
///         not the same as the keys the transaction touched. Writing a value that a higher
///         layer already overrides changes the database and changes nothing anybody can
///         observe, so it appears here not at all — and a commit that changes nothing raises
///         no event whatsoever. That is the rule the notification tests pin down, and it is
///         what stops a managed machine from waking every application each time a user
///         re-sets a preference that policy is holding.
///     </para>
/// </remarks>
public sealed class SettingsChangedEventArgs : EventArgs {
    /// <summary>A commit that moved something.</summary>
    /// <param name="changes">The keys whose resolved value changed, and what they resolve to now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changes" /> is null.</exception>
    public SettingsChangedEventArgs(IReadOnlyList<SettingsResolution> changes) =>
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));

    /// <summary>The keys whose resolved value changed. Never empty.</summary>
    public IReadOnlyList<SettingsResolution> Changes { get; }

    /// <summary>Whether this commit touched any key in a schema.</summary>
    /// <param name="schemaId">The schema identifier to look for.</param>
    public bool Touches(string schemaId) {
        foreach (var change in Changes) {
            if (string.Equals(change.Key.Schema, schemaId, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}

namespace Trinix.Settings;

/// <summary>
///     A key's full identity: which schema it belongs to, and its name within that schema.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 asks for per-user, per-app namespacing. The per-user half is the database
///         file; this is the per-app half, and it is a pair rather than a joined string so
///         that the two halves cannot be confused by a name containing a dot — which every
///         schema identifier does.
///     </para>
///     <para>
///         Applications do not normally construct these. The generator emits a
///         <c>Keys</c> class per schema whose members are the only spelling of each key in
///         the program, so that a typo is a compile error rather than a preference that
///         reads its default forever. ⚠ That is the whole argument for the generator over a
///         <c>store.Get("com.example.app", "AcentColor")</c> API: the misspelling is not an
///         error anywhere, it is just a key nobody ever wrote.
///     </para>
/// </remarks>
/// <param name="Schema">The schema identifier, e.g. <c>com.trinix.desktop.appearance</c>.</param>
/// <param name="Name">The key's name within that schema, e.g. <c>AccentColor</c>.</param>
public readonly record struct SettingsKey(string Schema, string Name) {
    /// <inheritdoc />
    public override string ToString() => Schema + "/" + Name;
}

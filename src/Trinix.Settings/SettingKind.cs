namespace Trinix.Settings;

/// <summary>
///     The complete set of things a preference may be.
/// </summary>
/// <remarks>
///     <para>
///         Five kinds, and the shortness of the list is the design. Doc 02 § Settings
///         storage draws the line between a preference and application data at the schema:
///         a preference is small, user-meaningful and resettable. Every kind here is a
///         single scalar a Settings pane can render as one control and a person can be
///         shown in one row.
///     </para>
///     <para>
///         ⚠ <b>There is deliberately no list, no dictionary and no blob.</b> A list is the
///         shape that turns a settings store into a registry: the moment a key holds a
///         list it holds records, records grow fields, and the fields are not in any schema
///         — which is doc 02's definition of data. Applications that genuinely need an
///         ordered collection have a container to put it in. If this ever has to change,
///         change it here and read that paragraph again first.
///     </para>
///     <para>
///         An <see cref="Enum" /> is stored by member <i>name</i> rather than by number.
///         Two reasons, and the second is the one that matters: a settings database is
///         looked at with the <c>sqlite3</c> CLI on a machine that may have nothing but a
///         serial console, and <c>Dark</c> is an answer where <c>1</c> is a question; and
///         an enum whose members are renumbered by an innocent-looking edit would otherwise
///         silently repoint every user's setting at a different member.
///     </para>
/// </remarks>
public enum SettingKind {
    /// <summary>
    ///     Not a value. ⚠ A default-constructed <see cref="SettingValue" /> has this kind,
    ///     which is why every accessor on it checks: a zero-initialised struct that claimed
    ///     to be <c>false</c> would be a default nobody declared.
    /// </summary>
    None = 0,

    /// <summary><see cref="bool" />. Stored as SQLite INTEGER 0 or 1.</summary>
    Bool = 1,

    /// <summary><see cref="long" /> (an <see cref="int" /> property widens to it). Stored as SQLite INTEGER.</summary>
    Integer = 2,

    /// <summary><see cref="double" />. Stored as SQLite REAL.</summary>
    Real = 3,

    /// <summary><see cref="string" />, never null. Stored as SQLite TEXT.</summary>
    Text = 4,

    /// <summary>A member of a CLR enum, by name. Stored as SQLite TEXT.</summary>
    Enum = 5,
}

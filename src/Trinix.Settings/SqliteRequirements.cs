using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Trinix.Settings;

/// <summary>
///     What the loaded SQLite has to be, and the cheap check that says so at open time.
/// </summary>
/// <remarks>
///     <para>
///         Trinix's target reaches SQLite through SQLitePCLRaw's <i>system</i> provider,
///         against the <c>libsqlite3.so.0</c> that <c>base/recipes/sqlite</c> builds. A
///         development host has no such library, so the tests run against SQLitePCLRaw's
///         own <c>e_sqlite3</c> — a different build of the same amalgamation, with a
///         different set of compile-time flags. That is a real seam, and a suite that
///         silently proved a different SQLite than the one that ships would be worth less
///         than it appears.
///     </para>
///     <para>
///         ⚠ <b>This class is what narrows it.</b> <c>PRAGMA compile_options</c> costs one
///         statement on a connection that is being opened anyway and answers the only
///         question that matters: is the library underneath the one the recipe describes.
///         Four lists, and the split between them is the argument:
///     </para>
///     <list type="bullet">
///         <item><description>
///             <see cref="Required" /> — what <i>this store</i> cannot work without.
///             Missing one of these throws.
///         </description></item>
///         <item><description>
///             <see cref="Forbidden" /> — the <c>SQLITE_OMIT_*</c> flags the recipe
///             explicitly declines to set, each because a managed caller depends on it.
///             Present, they throw. They are absent from both builds today; the check is
///             for the day somebody reads sqlite.org's "recommended compile-time options"
///             page and applies it to a shared system library.
///         </description></item>
///         <item><description>
///             <see cref="RecipePromises" /> — everything <c>trinix_check</c> proves,
///             including FTS5, which this component never uses. Reported by
///             <see cref="Shortfall" /> rather than thrown, because a desktop that will
///             not start its settings store because doc 06's search feature is missing has
///             turned one broken thing into two.
///         </description></item>
///         <item><description>
///             <see cref="Divergences" /> — what the bundled build has and the shipped one
///             will not. Nothing throws on these; they are the list of things the store must
///             not depend on, pinned by a test so a new one arrives as a red build.
///         </description></item>
///     </list>
///     <para>
///         ⚠ Just as important, and easier to miss: <b>the store never relies on a
///         compile-time default</b>, because the two builds do not agree about them.
///         <c>e_sqlite3</c> sets <c>SQLITE_DEFAULT_FOREIGN_KEYS</c> and the recipe's library
///         does not, so foreign key enforcement — which is how a write to an undeclared key
///         is refused — would be on here and off on the device. <see cref="Verify" />
///         therefore checks the *runtime* pragma state after the store has set it, rather
///         than checking a compile option and hoping. Journal mode and synchronous are set
///         explicitly for the same reason.
///     </para>
///     <para>
///         ⚠ <b><see cref="Divergences" /> is what the first run of this check produced, and
///         it is not decoration.</b> <c>SQLITE_DQS=0</c> — which turns a double-quoted string
///         literal from a silent fallback into an error — was in <see cref="Forbidden" /> in
///         the first draft, reasoning from the recipe naming it among the flags it declines
///         to set. That was wrong, and the store refused to open on the first test: the
///         bundled build has it, the recipe's library will not, and the recipe's point is
///         that the SQL dialect belongs to whoever owns the schema — not that a stricter
///         library is a broken one. Refusing to start on the safer build would be enforcing
///         the recipe's prose against its own reasoning. What the divergence does cost is one
///         hazard worth writing down: on the target a mistyped identifier inside double
///         quotes is not an error, it is a string literal, so a typo that fails loudly in
///         these tests would misbehave quietly on a device. The store's SQL therefore
///         contains no double quotes at all.
///     </para>
/// </remarks>
public static class SqliteRequirements {
    /// <summary>
    ///     Compile options this store cannot run without.
    /// </summary>
    /// <remarks>
    ///     <c>ENABLE_COLUMN_METADATA</c> is here because Microsoft.Data.Sqlite's
    ///     <c>GetSchemaTable</c> and <c>GetColumnSchema</c> are built on
    ///     <c>sqlite3_column_table_name()</c> and friends, which exist only under that flag;
    ///     the recipe names Trinix's three managed callers as the reason it is set.
    ///     <c>THREADSAFE=1</c> because the store is a service with a connection reached from
    ///     more than one thread, and SQLITE_THREADSAFE=0 would make that a data race rather
    ///     than an error.
    /// </remarks>
    public static IReadOnlyList<string> Required { get; } = [
        "ENABLE_COLUMN_METADATA",
        "THREADSAFE=1",
    ];

    /// <summary>
    ///     Compile options whose presence means the library was built for somebody else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three of the recipe's "deliberately not set" list, each with a named managed
    ///         consumer: <c>OMIT_DECLTYPE</c> breaks Microsoft.Data.Sqlite's column type
    ///         mapping, <c>OMIT_DEPRECATED</c> is the same problem one release later, and
    ///         <c>OMIT_AUTOINIT</c> moves <c>sqlite3_initialize()</c> into the caller's
    ///         contract so that a caller predating the change crashes rather than fails.
    ///         None is present in either build today; the check is for the day somebody
    ///         reads sqlite.org's "recommended compile-time options" page and applies it to
    ///         a shared system library.
    ///     </para>
    ///     <para>
    ///         ⚠ The recipe's fourth entry, <c>DQS=0</c>, is deliberately <i>not</i> here.
    ///         See the type's remarks: it is a divergence between the two builds rather than
    ///         a defect in either, and treating it as forbidden made the store refuse to
    ///         start on the stricter library.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Forbidden { get; } = [
        "OMIT_DECLTYPE",
        "OMIT_DEPRECATED",
        "OMIT_AUTOINIT",
    ];

    /// <summary>
    ///     Compile options the bundled development build has and the shipped one will not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The whole point of writing these down is that each is something the store
    ///         must not depend on, because depending on it would pass every test on a
    ///         development host and fail on a device. They are pinned by a test so that a new
    ///         one arrives as a red build rather than as a surprise on a serial console.
    ///     </para>
    ///     <list type="bullet">
    ///         <item><description>
    ///             <c>DEFAULT_FOREIGN_KEYS</c> — foreign keys default to on here and off in a
    ///             stock build. The store sends the pragma and <see cref="Verify" /> reads it
    ///             back.
    ///         </description></item>
    ///         <item><description>
    ///             <c>DQS=0</c> — double-quoted string literals are an error here and a silent
    ///             fallback there, so a mistyped identifier is caught here and quietly becomes
    ///             a string on the target. The store's SQL uses no double quotes.
    ///         </description></item>
    ///         <item><description>
    ///             <c>ENABLE_FTS3</c>, <c>ENABLE_FTS4</c>, <c>ENABLE_RTREE</c> — present here,
    ///             absent from the recipe, which asks for FTS5 only. Nothing in Trinix may use
    ///             them.
    ///         </description></item>
    ///     </list>
    /// </remarks>
    public static IReadOnlyList<string> Divergences { get; } = [
        "DEFAULT_FOREIGN_KEYS",
        "DQS=0",
        "ENABLE_FTS3",
        "ENABLE_FTS4",
        "ENABLE_RTREE",
    ];

    /// <summary>
    ///     Everything <c>base/recipes/sqlite</c>'s <c>trinix_check</c> proves about the
    ///     library that ships.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>ENABLE_FTS5</c> is in this list and not in <see cref="Required" />, and the
    ///     difference is the point. The settings store never runs a <c>MATCH</c>; doc 06's
    ///     index does, and it is the same shared object. A store that refused to open
    ///     without FTS5 would turn "search is broken" into "the desktop has no preferences",
    ///     so the shortfall is something a caller reports rather than something this throws.
    /// </remarks>
    public static IReadOnlyList<string> RecipePromises { get; } = [
        "ENABLE_COLUMN_METADATA",
        "ENABLE_FTS5",
        "THREADSAFE=1",
    ];

    /// <summary>
    ///     The native library SQLitePCLRaw actually loaded.
    /// </summary>
    /// <remarks>
    ///     <c>sqlite3</c> is the system provider — on a Trinix image, the recipe's
    ///     <c>libsqlite3.so.0</c>. <c>e_sqlite3</c> is SQLitePCLRaw's own build, which is
    ///     what a development host gets. ⚠ This string is the single fact that tells the two
    ///     worlds apart at runtime, which is why it is in every failure message here.
    /// </remarks>
    public static string NativeLibrary => SQLitePCL.raw.GetNativeLibraryName();

    /// <summary>
    ///     <c>PRAGMA compile_options</c>, verbatim, in the order SQLite returns it.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection" /> is null.</exception>
    public static IReadOnlyList<string> CompileOptions(SqliteConnection connection) {
        ArgumentNullException.ThrowIfNull(connection);

        var options = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA compile_options";
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            options.Add(reader.GetString(0));
        }

        return options;
    }

    /// <summary>
    ///     Which of <see cref="RecipePromises" /> this build does not keep.
    /// </summary>
    /// <remarks>
    ///     Empty on a correct image and — as it happens — empty on SQLitePCLRaw's
    ///     <c>e_sqlite3</c> too, which is worth knowing rather than assuming: it is why the
    ///     development host's results about column metadata and FTS5 transfer at all.
    /// </remarks>
    /// <param name="options">What <see cref="CompileOptions" /> returned.</param>
    public static IReadOnlyList<string> Shortfall(IReadOnlyList<string> options) {
        ArgumentNullException.ThrowIfNull(options);

        var missing = new List<string>();
        foreach (var promise in RecipePromises) {
            if (!Contains(options, promise)) {
                missing.Add(promise);
            }
        }

        return missing;
    }

    /// <summary>
    ///     Refuse a SQLite this store cannot run on, before anything depends on it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The foreign-key check is not a compile option and is the most valuable line
    ///     here. The store enforces "a value may only exist for a declared key" with a
    ///     foreign key rather than an <c>if</c>, so that the rule cannot drift from an
    ///     in-memory copy of the schema — and foreign keys are off by default in a stock
    ///     SQLite and on by default in <c>e_sqlite3</c>. Checking the pragma's *runtime*
    ///     value is the difference between a rule that holds on a laptop and a rule that
    ///     holds on the device.
    /// </remarks>
    /// <param name="connection">An open connection, already configured by the store.</param>
    /// <exception cref="SettingsProviderException">The library is unusable.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="connection" /> is null.</exception>
    public static void Verify(SqliteConnection connection) {
        ArgumentNullException.ThrowIfNull(connection);

        var options = CompileOptions(connection);
        var problems = new List<string>();

        foreach (var required in Required) {
            if (!Contains(options, required)) {
                problems.Add($"  SQLITE_{required} is not compiled in.");
            }
        }

        foreach (var forbidden in Forbidden) {
            if (Contains(options, forbidden)) {
                problems.Add($"  SQLITE_{forbidden} is compiled in, and a managed caller depends on it not being.");
            }
        }

        if (!ScalarIsOne(connection, "PRAGMA foreign_keys")) {
            problems.Add(
                "  foreign key enforcement is off on this connection, so a value could be written "
                + "for a key no schema declares. It is off by default in a stock SQLite and on by "
                + "default in e_sqlite3; the store sets it explicitly for exactly that reason."
            );
        }

        if (problems.Count > 0) {
            throw new SettingsProviderException(NativeLibrary, problems);
        }
    }

    static bool ScalarIsOne(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is not null
               && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
    }

    // Ordinal, and a plain loop: the option strings are ASCII identifiers produced by
    // SQLite itself, and this runs once per connection.
    static bool Contains(IReadOnlyList<string> options, string wanted) {
        foreach (var option in options) {
            if (string.Equals(option, wanted, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}

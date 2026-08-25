using System.Collections.Generic;

namespace Trinix.Settings;

/// <summary>
///     Everything this library throws that is about settings rather than about I/O.
/// </summary>
/// <remarks>
///     One base class so that a Settings pane, or the service that will front this store,
///     can catch the category without catching <see cref="System.IO.IOException" /> as well.
///     ⚠ SQLite's own failures are deliberately <i>not</i> wrapped: a
///     <c>SqliteException</c> carries a result code that names the problem — SQLITE_BUSY,
///     SQLITE_CORRUPT, SQLITE_READONLY are three very different mornings — and re-throwing
///     it as "settings failed" would be discarding the only useful part.
/// </remarks>
public class SettingsException : Exception {
    /// <summary>A settings failure with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public SettingsException(string message) : base(message) { }

    /// <summary>A settings failure with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public SettingsException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
///     A key was used that no registered schema declares.
/// </summary>
/// <remarks>
///     Doc 02: "A key with no schema is a bug the generator catches." This is what happens
///     on the paths the generator never saw — a schema built at runtime, a key from a
///     configuration file, a component that forgot to <see cref="SettingsStore.Register" />.
///     ⚠ It is an exception rather than a silently-returned default because a default
///     returned for a key nobody declared is indistinguishable from the setting working.
/// </remarks>
public sealed class SettingsSchemaException : SettingsException {
    /// <summary>An undeclared key.</summary>
    /// <param name="key">The key that has no descriptor.</param>
    public SettingsSchemaException(SettingsKey key)
        : base($"'{key}' is not in any registered schema. Doc 02: a key with no schema is a bug. "
               + "Declare it on a [SettingsSchema] interface, or register the schema before using it.") =>
        Key = key;

    /// <summary>A conflicting registration.</summary>
    /// <param name="message">What disagrees with what.</param>
    public SettingsSchemaException(string message) : base(message) { }

    /// <summary>The key, when there was one.</summary>
    public SettingsKey Key { get; }
}

/// <summary>
///     A <see cref="SettingValue" /> was read as the wrong kind.
/// </summary>
public sealed class SettingsTypeMismatchException : SettingsException {
    /// <summary>A kind mismatch.</summary>
    /// <param name="expected">The kind the caller asked for.</param>
    /// <param name="actual">The kind the value has.</param>
    public SettingsTypeMismatchException(SettingKind expected, SettingKind actual)
        : base($"This setting is {actual}, not {expected}.") {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The kind that was asked for.</summary>
    public SettingKind Expected { get; }

    /// <summary>The kind the value actually has.</summary>
    public SettingKind Actual { get; }
}

/// <summary>
///     The SQLite that got loaded is not one this store can run on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ This is the exception the provider seam exists to produce. Doc 02's store, doc
///         03's notification history and doc 06's index all reach the same
///         <c>libsqlite3.so.0</c>, and a managed caller discovers a missing compile-time
///         feature as a runtime failure somewhere deep inside a query — on a machine with a
///         serial console, which is the expensive way to find out. Thrown from
///         <see cref="SettingsStore.Open(SettingsLayout)" />, before any schema is
///         registered and before any value is read, so that the failure names the library
///         rather than the symptom.
///     </para>
///     <para>
///         The message carries the native library's name because that is the fact that
///         distinguishes the two worlds: <c>sqlite3</c> is the system provider against
///         base/recipes/sqlite's build, <c>e_sqlite3</c> is SQLitePCLRaw's own, and a
///         developer reading this on a laptop needs to know which one just failed.
///     </para>
/// </remarks>
public sealed class SettingsProviderException : SettingsException {
    /// <summary>An unusable SQLite build.</summary>
    /// <param name="nativeLibrary">The native library SQLitePCLRaw loaded.</param>
    /// <param name="problems">One line per requirement that is not met.</param>
    public SettingsProviderException(string nativeLibrary, IReadOnlyList<string> problems)
        : base(Describe(nativeLibrary, problems)) {
        NativeLibrary = nativeLibrary;
        Problems = problems;
    }

    /// <summary>The native library SQLitePCLRaw loaded — <c>sqlite3</c> or <c>e_sqlite3</c>.</summary>
    public string NativeLibrary { get; }

    /// <summary>One line per unmet requirement.</summary>
    public IReadOnlyList<string> Problems { get; }

    static string Describe(string nativeLibrary, IReadOnlyList<string> problems) =>
        $"The loaded SQLite ('{nativeLibrary}') cannot back a Trinix settings store:"
        + Environment.NewLine
        + string.Join(Environment.NewLine, problems ?? Array.Empty<string>())
        + Environment.NewLine
        + "base/recipes/sqlite builds the library Trinix ships and its trinix_check proves "
        + "these; if this fires on a device, the image is carrying somebody else's libsqlite3.";
}

namespace Trinix.Settings.Tests;

/// <summary>
///     A directory with up to four settings databases in it, removed when the test ends.
/// </summary>
/// <remarks>
///     <para>
///         Real files, not <c>:memory:</c>, and that is the point of this type existing at
///         all. Two of the three properties doc 02 asks the store for are properties of a
///         file: a transaction is atomic because a rollback journal or a WAL is on a disk,
///         and a second store over the same paths is what makes layering observable. An
///         in-memory database is private to its connection, so an "atomicity" test over one
///         would prove that a <c>List&lt;T&gt;</c> can be discarded.
///     </para>
///     <para>
///         ⚠ Each instance gets its own directory under the temporary path. Sharing one
///         would make the suite order-dependent in a way that only shows up when xunit
///         decides to run classes in parallel — which it does by default.
///     </para>
/// </remarks>
sealed class TemporaryLayout : IDisposable {
    readonly string _directory;

    public TemporaryLayout() {
        _directory = Path.Combine(Path.GetTempPath(), "trinix-settings-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        Layout = SettingsLayout.ForDirectory(_directory);
    }

    /// <summary>All four layer paths, side by side in this directory.</summary>
    public SettingsLayout Layout { get; }

    /// <summary>Open a store over this layout.</summary>
    public SettingsStore Open(SettingsLayer writable = SettingsLayer.User) =>
        SettingsStore.Open(Layout, writable);

    /// <summary>
    ///     Put a value into a layer other than the one under test, the way an administrator
    ///     or a management profile would.
    /// </summary>
    /// <remarks>
    ///     Through the store's own write path rather than by hand-writing SQL: an
    ///     administrator database is produced by the same code, and a test that built one
    ///     with a different INSERT would be testing a file shape nothing creates.
    /// </remarks>
    public void Seed(SettingsLayer layer, SettingsSchema schema, SettingsKey key, SettingValue value) {
        using var store = Open(layer);
        store.Register(schema);
        store.BeginWrite().Set(key, value).Commit();
    }

    public void Dispose() {
        try {
            Directory.Delete(_directory, recursive: true);
        } catch (IOException) {
            // A leaked temporary directory is not worth failing a green test over; the
            // operating system reaps it. A failure *here* would replace the real result.
        }
    }
}

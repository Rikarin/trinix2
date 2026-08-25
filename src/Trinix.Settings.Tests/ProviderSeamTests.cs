using Microsoft.Data.Sqlite;

namespace Trinix.Settings.Tests;

/// <summary>
///     The seam between the SQLite these tests run against and the one Trinix ships.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These tests do not prove that the shipped library is correct.</b> Nothing
///         runnable on a macOS host can: the target loads <c>libsqlite3.so.0</c> from
///         <c>base/recipes/sqlite</c> through SQLitePCLRaw's system provider, and there is no
///         such library here, so everything below runs against SQLitePCLRaw's bundled
///         <c>e_sqlite3</c>. What they prove is narrower and still worth having:
///     </para>
///     <list type="number">
///         <item><description>
///             that the startup assertion exists, runs on every open, and <i>can fail</i> —
///             a check nobody has seen fail is a check nobody has tested;
///         </description></item>
///         <item><description>
///             that the requirements it checks are the ones the recipe's own
///             <c>trinix_check</c> proves, so the two cannot drift apart silently;
///         </description></item>
///         <item><description>
///             that the divergences between the two builds which are known today are pinned
///             here, so a new one arrives as a red test rather than as a surprise on a
///             device with a serial console.
///         </description></item>
///     </list>
/// </remarks>
public sealed class ProviderSeamTests {
    /// <summary>The only two native libraries a Trinix build should ever be running on.</summary>
    static readonly string[] ExpectedLibraries = ["e_sqlite3", "sqlite3"];

    /// <summary>
    ///     Which SQLite this run is actually using, stated out loud.
    /// </summary>
    /// <remarks>
    ///     ⚠ The whole suite's meaning depends on this one string. <c>e_sqlite3</c> is
    ///     SQLitePCLRaw's own build and is what a development host gets; <c>sqlite3</c> is
    ///     the system provider, and on a Trinix image that is the recipe's library. Anything
    ///     else means the .csproj's RID condition selected a bundle nobody intended.
    /// </remarks>
    [Fact]
    public void TheLoadedLibraryIsOneOfTheTwoTrinixExpects() {
        SQLitePCL.Batteries_V2.Init();
        Assert.Contains(SqliteRequirements.NativeLibrary, ExpectedLibraries);
    }

    [Fact]
    public void TheLoadedLibraryKeepsEveryPromiseTheRecipeMakes() {
        using var connection = OpenBare(foreignKeys: true);
        var options = SqliteRequirements.CompileOptions(connection);

        // Empty on the image by construction — trinix_check refuses to install a library
        // without these. Empty on e_sqlite3 as a matter of fact, which is what makes any of
        // the column-metadata behaviour observed here transfer at all.
        Assert.Empty(SqliteRequirements.Shortfall(options));
    }

    [Fact]
    public void NothingTheRecipeDeclinedToSetIsCompiledIn() {
        using var connection = OpenBare(foreignKeys: true);
        var options = SqliteRequirements.CompileOptions(connection);

        foreach (var forbidden in SqliteRequirements.Forbidden) {
            Assert.DoesNotContain(forbidden, options);
        }
    }

    /// <summary>
    ///     The negative control: the assertion can fail.
    /// </summary>
    /// <remarks>
    ///     ⚠ A zero-warning result from a pipeline nobody has proved <i>can</i> warn is not
    ///     evidence, and the same is true of a startup check that has only ever passed.
    ///     Foreign keys are the cheapest way to make it fail honestly, and not a contrived
    ///     one: this is exactly the state the store would be in on the device if it trusted
    ///     the compile-time default instead of setting the pragma.
    /// </remarks>
    [Fact]
    public void VerifyRefusesAConnectionWithForeignKeysOff() {
        using var connection = OpenBare(foreignKeys: false);

        var refusal = Assert.Throws<SettingsProviderException>(() => SqliteRequirements.Verify(connection));
        Assert.Equal(SqliteRequirements.NativeLibrary, refusal.NativeLibrary);
        Assert.Contains(refusal.Problems, problem => problem.Contains("foreign key", StringComparison.Ordinal));
        Assert.Contains("base/recipes/sqlite", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPassesOnAConnectionTheStoreWouldHaveConfigured() {
        using var connection = OpenBare(foreignKeys: true);
        SqliteRequirements.Verify(connection);
    }

    /// <summary>
    ///     The store enforces foreign keys rather than inheriting them, observed from
    ///     outside.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the divergence that would have shipped. <c>e_sqlite3</c> is built with
    ///     <c>SQLITE_DEFAULT_FOREIGN_KEYS</c> and the recipe's library is not, so a store
    ///     that never sent the pragma would refuse an undeclared key here and accept it on
    ///     the device — a test suite proving a rule that does not hold where it matters.
    ///     Behavioural rather than a pragma read, because what has to be true is the
    ///     refusal, not the setting.
    /// </remarks>
    [Fact]
    public void TheStoreRefusesAnUndeclaredKeyWhicheverBuildItIsOn() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        Assert.Throws<SettingsSchemaException>(() => store.BeginWrite()
            .Set(new SettingsKey(SampleSettings.SchemaId, "NeverDeclared"), SettingValue.Text("x"))
            .Commit());
    }

    /// <summary>
    ///     The known differences between the bundled build and the shipped one, written down
    ///     where they will be re-read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ This test only asserts anything when it is running on <c>e_sqlite3</c>, and
    ///         that is deliberate rather than lazy: its subject is the development host's
    ///         library, and on the target the same facts are false and irrelevant. Each entry
    ///         is something the store must therefore not depend on:
    ///     </para>
    ///     <para>
    ///         <c>SqliteRequirements.Divergences</c> lists them and says what each one costs.
    ///         The list is not hypothetical: <c>DQS=0</c> is on it because the first run of
    ///         this suite had it in the <i>forbidden</i> list instead, and every test in the
    ///         project failed at <c>SettingsStore.Open</c> with the assertion's own message.
    ///         That is the check working.
    ///     </para>
    ///     <para>
    ///         The direction that would actually hurt — something present on the target and
    ///         missing here — is covered by <see cref="TheLoadedLibraryKeepsEveryPromiseTheRecipeMakes" />
    ///         and by the recipe's own <c>trinix_check</c>, which is the evidence for the
    ///         shipped library and is not this file.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheBundledBuildDivergesFromTheShippedOneInExactlyTheseKnownWays() {
        using var connection = OpenBare(foreignKeys: true);
        var options = SqliteRequirements.CompileOptions(connection);

        if (SqliteRequirements.NativeLibrary != "e_sqlite3") {
            // Running on the system provider: the divergence list is not about this library.
            // Assert the one thing that must hold everywhere and stop.
            Assert.Empty(SqliteRequirements.Shortfall(options));
            return;
        }

        foreach (var divergence in SqliteRequirements.Divergences) {
            Assert.Contains(divergence, options);
        }
    }

    /// <summary>
    ///     A bare connection, configured the way the store configures its own — or, for the
    ///     negative control, not.
    /// </summary>
    static SqliteConnection OpenBare(bool foreignKeys) {
        SQLitePCL.Batteries_V2.Init();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = ":memory:",
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = foreignKeys ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF";
        command.ExecuteNonQuery();
        return connection;
    }
}

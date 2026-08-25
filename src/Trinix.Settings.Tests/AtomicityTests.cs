namespace Trinix.Settings.Tests;

/// <summary>
///     Doc 02's first reason for a database rather than a directory of files: an atomic
///     multi-key write.
/// </summary>
/// <remarks>
///     ⚠ Every refusal here comes from the database's own schema rather than from a check in
///     C#, and that is what makes these tests worth writing. The rule "a value may exist only
///     for a declared key" is a foreign key; "a value has the kind its key was declared with"
///     is a trigger. Written as an <c>if</c> in <c>SettingsStore</c>, both would hold for
///     callers that come through this library — which is not the same set as the callers
///     that come through <c>sqlite3</c> on a serial console.
/// </remarks>
public sealed class AtomicityTests {
    [Fact]
    public void EveryKeyInOneCommitLandsTogether() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("a").SetCount(1).SetEnabled(false).Commit();

        Assert.Equal("a", sample.Label);
        Assert.Equal(1, sample.Count);
        Assert.False(sample.Enabled);
    }

    /// <summary>
    ///     A write to a key nothing declares takes the whole transaction with it.
    /// </summary>
    /// <remarks>
    ///     The undeclared key is deliberately the <i>second</i> entry: the first has already
    ///     been applied inside the transaction when the second is refused, so a store that
    ///     was not transactional would leave it behind and pass every other test in this
    ///     file.
    /// </remarks>
    [Fact]
    public void AWriteToAnUndeclaredKeyLeavesNothingBehind() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("first").Commit();

        var refusal = Assert.Throws<SettingsSchemaException>(() => store.BeginWrite()
            .Set(SampleSettings.Keys.Label, SettingValue.Text("second"))
            .Set(new SettingsKey(SampleSettings.SchemaId, "NeverDeclared"), SettingValue.Text("x"))
            .Commit());

        Assert.Contains("Nothing in this transaction was written", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("first", sample.Label);
    }

    [Fact]
    public void AWriteOfTheWrongKindLeavesNothingBehind() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetCount(7).Commit();

        Assert.Throws<SettingsSchemaException>(() => store.BeginWrite()
            .Set(SampleSettings.Keys.Count, SettingValue.Integer(8))
            .Set(SampleSettings.Keys.Label, SettingValue.Integer(3))
            .Commit());

        Assert.Equal(7, sample.Count);
    }

    [Fact]
    public void ARolledBackTransactionLeavesTheStoreUsable() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        Assert.Throws<SettingsSchemaException>(() => store.BeginWrite()
            .Set(new SettingsKey(SampleSettings.SchemaId, "NeverDeclared"), SettingValue.Text("x"))
            .Commit());

        // ⚠ The point of the ROLLBACK in the failure path: without it the connection is
        // left inside a transaction and every later write fails with "cannot start a
        // transaction within a transaction", which is a much more confusing bug than the
        // one that caused it.
        sample.BeginEdit().SetLabel("still works").Commit();
        Assert.Equal("still works", sample.Label);
    }

    [Fact]
    public void TheSameKeySetTwiceInOneTransactionKeepsTheLastValue() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("first").SetLabel("second").Commit();

        Assert.Equal("second", sample.Label);
    }

    [Fact]
    public void ATransactionCannotBeCommittedTwice() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        var transaction = store.BeginWrite().Set(SampleSettings.Keys.Label, SettingValue.Text("once"));
        transaction.Commit();

        Assert.Throws<InvalidOperationException>(() => transaction.Commit());
        Assert.Throws<InvalidOperationException>(
            () => transaction.Set(SampleSettings.Keys.Label, SettingValue.Text("twice"))
        );
    }

    /// <summary>
    ///     Abandoning a transaction costs nothing and locks nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ The claim being pinned is not really "the value is unchanged" — it is that
    ///     nothing was held. A transaction that opened <c>BEGIN IMMEDIATE</c> on
    ///     construction would leave a write lock on the database for as long as the object
    ///     lived, so a second store opening the same file would block, and this test would
    ///     time out rather than fail.
    /// </remarks>
    [Fact]
    public void AnAbandonedTransactionHoldsNothing() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        _ = store.BeginWrite().Set(SampleSettings.Keys.Label, SettingValue.Text("never committed"));

        using var other = temporary.Open();
        var otherSample = new SampleSettings(other);
        otherSample.BeginEdit().SetLabel("from elsewhere").Commit();

        Assert.Equal("from elsewhere", sample.Label);
    }
}

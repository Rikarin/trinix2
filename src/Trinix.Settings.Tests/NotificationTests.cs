using System.Collections.Generic;

namespace Trinix.Settings.Tests;

/// <summary>
///     Doc 02's second reason for a database: "changes are events, not polls" — and exactly
///     one event per change.
/// </summary>
public sealed class NotificationTests {
    [Fact]
    public void AMultiKeyCommitRaisesExactlyOneEvent() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        var events = Record(store);
        sample.BeginEdit().SetLabel("a").SetCount(1).SetEnabled(false).Commit();

        // One event, three keys. ⚠ Not three events: the write was atomic in the database,
        // and a subscriber that woke three times would see two torn views of it.
        var raised = Assert.Single(events);
        Assert.Equal(3, raised.Changes.Count);
        Assert.Contains(raised.Changes, change => change.Key == SampleSettings.Keys.Label);
        Assert.Contains(raised.Changes, change => change.Key == SampleSettings.Keys.Count);
        Assert.Contains(raised.Changes, change => change.Key == SampleSettings.Keys.Enabled);
    }

    [Fact]
    public void ASingleKeyCommitRaisesExactlyOneEventNamingItsNewValueAndLayer() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        var events = Record(store);
        sample.BeginEdit().SetLabel("moved").Commit();

        var change = Assert.Single(Assert.Single(events).Changes);
        Assert.Equal(SampleSettings.Keys.Label, change.Key);
        Assert.Equal("moved", change.Value.AsText());
        Assert.Equal(SettingsLayer.User, change.Layer);
    }

    [Fact]
    public void WritingTheValueThatIsAlreadyThereRaisesNothing() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("same").Commit();

        var events = Record(store);
        sample.BeginEdit().SetLabel("same").Commit();

        Assert.Empty(events);
    }

    /// <summary>
    ///     A write whose resolved value is overridden by a higher layer changes the database
    ///     and notifies nobody.
    /// </summary>
    /// <remarks>
    ///     ⚠ The rule is "the <i>resolved</i> value moved", not "a row changed". Without it,
    ///     a managed machine wakes every subscribed application each time a user re-sets a
    ///     preference that policy is holding — an event storm caused by nothing happening.
    /// </remarks>
    [Fact]
    public void AWriteThatChangesNoAnswerRaisesNothing() {
        using var temporary = new TemporaryLayout();
        temporary.Seed(SettingsLayer.ManagedProfile, SampleSettings.Schema,
            SampleSettings.Keys.Label, SettingValue.Text("policy"));

        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        var events = Record(store);
        sample.BeginEdit().SetLabel("mine").Commit();

        Assert.Empty(events);
        Assert.Equal("policy", sample.Label);
    }

    [Fact]
    public void ACommitThatMovesOneKeyOfThreeReportsOnlyThatKey() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("a").SetCount(1).Commit();

        var events = Record(store);
        sample.BeginEdit().SetLabel("a").SetCount(1).SetEnabled(false).Commit();

        var change = Assert.Single(Assert.Single(events).Changes);
        Assert.Equal(SampleSettings.Keys.Enabled, change.Key);
    }

    [Fact]
    public void ARefusedCommitRaisesNothing() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        var events = Record(store);
        Assert.Throws<SettingsSchemaException>(() => store.BeginWrite()
            .Set(SampleSettings.Keys.Label, SettingValue.Text("would have moved"))
            .Set(new SettingsKey(SampleSettings.SchemaId, "NeverDeclared"), SettingValue.Text("x"))
            .Commit());

        Assert.Empty(events);
    }

    [Fact]
    public void AResetThatRestoresTheDefaultIsAChange() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetCount(99).Commit();

        var events = Record(store);
        sample.BeginEdit().ResetCount().Commit();

        var change = Assert.Single(Assert.Single(events).Changes);
        Assert.Equal(4L, change.Value.AsInteger());
        Assert.Null(change.Layer);
    }

    [Fact]
    public void AResetOfAKeyThatWasNeverSetRaisesNothing() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        var events = Record(store);
        sample.BeginEdit().ResetAll().Commit();

        Assert.Empty(events);
    }

    [Fact]
    public void AGeneratedSubscriptionFiresOncePerCommitAndStopsWhenDisposed() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        var seen = new List<string>();
        var subscription = sample.Subscribe(settings => seen.Add(settings.Label));

        sample.BeginEdit().SetLabel("first").SetCount(2).Commit();
        sample.BeginEdit().SetLabel("second").Commit();

        subscription.Dispose();
        sample.BeginEdit().SetLabel("unheard").Commit();

        // ⚠ Disposing twice must be safe: a Settings pane that is torn down twice would
        // otherwise unhook a handler that is not its own.
        subscription.Dispose();

        Assert.Equal(["first", "second"], seen);
    }

    /// <summary>
    ///     A subscription only hears about its own schema.
    /// </summary>
    [Fact]
    public void ASubscriptionIgnoresOtherSchemas() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);
        var appearance = new AppearanceSettings(store);

        var sampleWoke = 0;
        using var subscription = sample.Subscribe(_ => sampleWoke++);

        appearance.BeginEdit().SetAccentColor("#ff0000").Commit();
        Assert.Equal(0, sampleWoke);

        sample.BeginEdit().SetLabel("mine").Commit();
        Assert.Equal(1, sampleWoke);
    }

    static List<SettingsChangedEventArgs> Record(SettingsStore store) {
        var events = new List<SettingsChangedEventArgs>();
        store.Changed += (_, args) => events.Add(args);
        return events;
    }
}

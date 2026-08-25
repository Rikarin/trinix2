namespace Trinix.Settings.Tests;

/// <summary>
///     Opening a store, declaring a schema, and the round trip of each kind.
/// </summary>
public sealed class SettingsStoreTests {
    [Fact]
    public void OpeningCreatesTheUserDatabaseAndNothingElse() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();

        Assert.True(File.Exists(temporary.Layout.UserPath));

        // ⚠ ATTACH creates the file it is pointed at, so a store that attached
        // unconditionally would leave an empty policy database behind on every machine
        // that ever started a session. The store checks File.Exists first; this is what
        // says so.
        Assert.False(File.Exists(temporary.Layout.AdministratorPath!));
        Assert.False(File.Exists(temporary.Layout.ManagedProfilePath!));
        Assert.False(File.Exists(temporary.Layout.SystemDefaultPath!));
    }

    [Fact]
    public void AKeyNoSchemaDeclaresIsRefused() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();

        var refusal = Assert.Throws<SettingsSchemaException>(
            () => store.Get(new SettingsKey("com.trinix.test.sample", "Enabled"))
        );
        Assert.Contains("a key with no schema is a bug", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredKeyReadsItsDefaultFromNoLayer() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        Assert.True(sample.Enabled);
        Assert.Equal(4, sample.Count);
        Assert.Equal(90L, sample.Timeout);
        Assert.Equal(0.5, sample.Opacity);
        Assert.Equal("sample", sample.Label);
        Assert.Equal(Corner.TopLeft, sample.Origin);

        // Null layer, not SystemDefault: "nobody has ever set this" is a different fact
        // from "the image set it", and doc 08's panes need to tell them apart.
        Assert.Null(sample.ResolveLabel().Layer);
    }

    [Fact]
    public void EveryKindSurvivesTheRoundTrip() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit()
            .SetEnabled(false)
            .SetCount(11)
            .SetTimeout(-3)
            .SetOpacity(0.125)
            .SetLabel("something else")
            .SetOrigin(Corner.BottomLeft)
            .Commit();

        Assert.False(sample.Enabled);
        Assert.Equal(11, sample.Count);
        Assert.Equal(-3L, sample.Timeout);
        Assert.Equal(0.125, sample.Opacity);
        Assert.Equal("something else", sample.Label);
        Assert.Equal(Corner.BottomLeft, sample.Origin);
    }

    [Fact]
    public void WhatWasWrittenIsStillThereAfterAReopen() {
        using var temporary = new TemporaryLayout();

        using (var first = temporary.Open()) {
            new SampleSettings(first).BeginEdit().SetLabel("persisted").Commit();
        }

        using var second = temporary.Open();
        Assert.Equal("persisted", new SampleSettings(second).Label);
    }

    [Fact]
    public void ResetRemovesTheValueSoTheDefaultAnswersAgain() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetCount(99).Commit();
        Assert.Equal(SettingsLayer.User, sample.ResolveCount().Layer);

        sample.BeginEdit().ResetCount().Commit();
        Assert.Equal(4, sample.Count);
        Assert.Null(sample.ResolveCount().Layer);
    }

    [Fact]
    public void AnEnumIsStoredByNameRatherThanByNumber() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetOrigin(Corner.TopRight).Commit();

        // The stored form, not the typed one: a settings database is read with the sqlite3
        // CLI on machines that have a serial console and nothing else, and a renumbering of
        // this enum must not silently repoint every user's value.
        Assert.Equal("TopRight", store.Get(SampleSettings.Keys.Origin).AsEnumMember());
    }

    [Fact]
    public void AnEnumMemberThisBuildDoesNotKnowFallsBackToTheDefault() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        // What a hand edit, or a database written by a later version, looks like.
        store.BeginWrite().Set(SampleSettings.Keys.Origin, SettingValue.EnumMember("Chartreuse")).Commit();

        Assert.Equal(Corner.TopLeft, sample.Origin);
    }

    [Fact]
    public void RegisteringTheSameSchemaTwiceIsFine() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();

        _ = new SampleSettings(store);
        _ = new SampleSettings(store);

        Assert.Single(store.Schemas);
    }

    [Fact]
    public void RegisteringADifferentSchemaUnderTheSameIdentifierIsRefused() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        var refusal = Assert.Throws<SettingsSchemaException>(() => store.Register(new SettingsSchema(
            SampleSettings.SchemaId,
            [new SettingDescriptor("Label", SettingValue.Text("different"), "A different declaration.")]
        )));
        Assert.Contains("already registered", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASchemaIdentifierThatIsNotReverseDnsIsRefused() {
        foreach (var bad in new[] { "", "appearance", "Com.Trinix.App", "com..trinix", "com.trinix.", "com.trin_ix" }) {
            Assert.False(SettingsSchema.IsWellFormedId(bad), bad);
        }

        Assert.True(SettingsSchema.IsWellFormedId("com.trinix.desktop.appearance"));
        Assert.True(SettingsSchema.IsWellFormedId("io.trinix-2.app"));
    }

    /// <summary>
    ///     The runtime half of doc 02's "if you cannot describe it in a settings schema, it
    ///     is data".
    /// </summary>
    /// <remarks>
    ///     The generator refuses a summaryless key at compile time; this is the same rule on
    ///     the path the generator never sees — a schema built by an importer or a test. A
    ///     rule enforced only where it is convenient is a rule with a documented hole.
    /// </remarks>
    [Fact]
    public void ADescriptorWithNoSummaryIsRefused() {
        var refusal = Assert.Throws<ArgumentException>(
            () => new SettingDescriptor("Something", SettingValue.Bool(true), "   ")
        );
        Assert.Contains("preference and application data", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptorWithNoDefaultIsRefused() {
        var refusal = Assert.Throws<ArgumentException>(
            () => new SettingDescriptor("Something", default, "It does something.")
        );
        Assert.Contains("a key with no schema is a bug", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingAValueAsTheWrongKindNamesBothKinds() {
        var mismatch = Assert.Throws<SettingsTypeMismatchException>(() => SettingValue.Text("x").AsInteger());
        Assert.Equal(SettingKind.Integer, mismatch.Expected);
        Assert.Equal(SettingKind.Text, mismatch.Actual);
    }

    [Fact]
    public void ADefaultConstructedValueIsNotAValue() {
        // ⚠ A zero-initialised struct that claimed to be `false` would be a default nobody
        // declared, silently answering reads for a key whose schema was never consulted.
        Assert.Equal(SettingKind.None, default(SettingValue).Kind);
        Assert.Throws<SettingsTypeMismatchException>(() => default(SettingValue).AsBool());
    }

    [Fact]
    public void TheSchemaIsAvailableForDocEightsSearchIndex() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        var schema = Assert.Single(store.Schemas);
        Assert.Equal(SampleSettings.SchemaId, schema.Id);
        Assert.All(schema.Settings, setting => Assert.False(string.IsNullOrWhiteSpace(setting.Summary)));
        Assert.Equal("How many sample things to keep.", schema.Find("Count")!.Summary);
    }
}

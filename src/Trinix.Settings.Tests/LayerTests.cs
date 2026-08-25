namespace Trinix.Settings.Tests;

/// <summary>
///     Doc 02's layering: system default → administrator → user → managed profile.
/// </summary>
/// <remarks>
///     Every case here seeds the non-user layers through the store's own write path, so what
///     is being read is a database of the same shape the administrative tooling would
///     produce. A test that hand-wrote the rows would be asserting about a file nothing
///     creates.
/// </remarks>
public sealed class LayerTests {
    static SettingsKey Label => SampleSettings.Keys.Label;

    static SettingsSchema Schema => SampleSettings.Schema;

    [Fact]
    public void WithNoLayerAtAllTheSchemaDefaultAnswers() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        var resolution = store.Resolve(Label);
        Assert.Equal("sample", resolution.Value.AsText());
        Assert.Null(resolution.Layer);
    }

    [Fact]
    public void EachLayerOverridesTheOneBelowIt() {
        using var temporary = new TemporaryLayout();

        // Seeded bottom-up, and asserted after each addition, so a failure names the layer
        // that stopped winning rather than only saying the answer is wrong.
        temporary.Seed(SettingsLayer.SystemDefault, Schema, Label, SettingValue.Text("image"));
        AssertResolves(temporary, "image", SettingsLayer.SystemDefault);

        temporary.Seed(SettingsLayer.Administrator, Schema, Label, SettingValue.Text("site"));
        AssertResolves(temporary, "site", SettingsLayer.Administrator);

        temporary.Seed(SettingsLayer.User, Schema, Label, SettingValue.Text("mine"));
        AssertResolves(temporary, "mine", SettingsLayer.User);

        // ⚠ The one that reads backwards until you say why: policy has to survive the user
        // opening Settings and changing it.
        temporary.Seed(SettingsLayer.ManagedProfile, Schema, Label, SettingValue.Text("policy"));
        AssertResolves(temporary, "policy", SettingsLayer.ManagedProfile);
    }

    [Fact]
    public void AUserWriteUnderAManagedProfileChangesTheDatabaseAndNotTheAnswer() {
        using var temporary = new TemporaryLayout();
        temporary.Seed(SettingsLayer.ManagedProfile, Schema, Label, SettingValue.Text("policy"));

        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("mine").Commit();

        Assert.Equal("policy", sample.Label);
        Assert.Equal(SettingsLayer.ManagedProfile, sample.ResolveLabel().Layer);

        // ...and it really did land in the user layer, which is what makes the managed
        // profile being lifted later restore the user's own choice rather than the default.
        using var withoutPolicy = SettingsStore.Open(
            temporary.Layout with { ManagedProfilePath = null },
            SettingsLayer.User
        );
        _ = new SampleSettings(withoutPolicy);
        Assert.Equal("mine", withoutPolicy.Get(Label).AsText());
    }

    /// <summary>
    ///     Reset means "drop what this layer says", not "set to the schema default".
    /// </summary>
    /// <remarks>
    ///     ⚠ On an unmanaged machine the two coincide, which is exactly why this needs a
    ///     test: the wrong implementation passes every case that has no administrator
    ///     database, and a site that sets a default would find every reset silently
    ///     discarding it.
    /// </remarks>
    [Fact]
    public void ResetFallsBackToTheLayerBelowAndNotToTheDefault() {
        using var temporary = new TemporaryLayout();
        temporary.Seed(SettingsLayer.Administrator, Schema, Label, SettingValue.Text("site"));

        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        sample.BeginEdit().SetLabel("mine").Commit();
        Assert.Equal("mine", sample.Label);

        sample.BeginEdit().ResetLabel().Commit();
        Assert.Equal("site", sample.Label);
        Assert.Equal(SettingsLayer.Administrator, sample.ResolveLabel().Layer);
    }

    [Fact]
    public void ALayerThatDoesNotDeclareTheKeyIsSimplySkipped() {
        using var temporary = new TemporaryLayout();
        temporary.Seed(SettingsLayer.SystemDefault, Schema, Label, SettingValue.Text("image"));

        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        // Count is in no layer at all; Label is in one. The same resolve statement answers
        // both, which is the point of it being one statement.
        Assert.Equal(4, sample.Count);
        Assert.Equal("image", sample.Label);
    }

    /// <summary>
    ///     A layer whose stored kind disagrees with the running declaration is skipped, not
    ///     thrown on.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is what an administrator database one version stale looks like. The
    ///     alternative behaviour — throwing — is a desktop that will not start because a
    ///     preference changed type, and a preference is not worth that.
    /// </remarks>
    [Fact]
    public void ALayerWhoseStoredKindIsStaleIsSkipped() {
        using var temporary = new TemporaryLayout();

        // The administrator database was authored when Label was a Bool.
        var stale = new SettingsSchema(
            SampleSettings.SchemaId,
            [new SettingDescriptor("Label", SettingValue.Bool(false), "What to call a sample thing.")]
        );
        temporary.Seed(SettingsLayer.Administrator, stale, Label, SettingValue.Bool(true));

        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        Assert.Equal("sample", sample.Label);
        Assert.Null(sample.ResolveLabel().Layer);
    }

    [Fact]
    public void AFileThatIsNotASettingsDatabaseIsDetachedRatherThanBreakingEveryRead() {
        using var temporary = new TemporaryLayout();
        File.WriteAllText(temporary.Layout.AdministratorPath!, "");

        using var store = temporary.Open();
        var sample = new SampleSettings(store);

        Assert.Equal("sample", sample.Label);
    }

    [Fact]
    public void AStoreWritesOnlyTheLayerItWasOpenedFor() {
        using var temporary = new TemporaryLayout();

        using (var administrator = temporary.Open(SettingsLayer.Administrator)) {
            administrator.Register(Schema);
            administrator.BeginWrite().Set(Label, SettingValue.Text("site")).Commit();
            Assert.Equal(SettingsLayer.Administrator, administrator.WritableLayer);
        }

        // The user database was never created by the administrator store: it attaches what
        // exists and writes only main.
        Assert.False(File.Exists(temporary.Layout.UserPath));
        Assert.True(File.Exists(temporary.Layout.AdministratorPath!));
    }

    static void AssertResolves(TemporaryLayout temporary, string expected, SettingsLayer layer) {
        using var store = temporary.Open();
        _ = new SampleSettings(store);

        var resolution = store.Resolve(Label);
        Assert.Equal(expected, resolution.Value.AsText());
        Assert.Equal(layer, resolution.Layer);
    }
}

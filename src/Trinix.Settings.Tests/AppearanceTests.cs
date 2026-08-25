namespace Trinix.Settings.Tests;

/// <summary>
///     Doc 02's own worked example, end to end: "an application that wants to follow the
///     accent colour subscribes."
/// </summary>
/// <remarks>
///     Everything here goes through the schema that ships in <c>Trinix.Settings</c> rather
///     than through the test assembly's own, so this is also the check that the generator's
///     output compiles and behaves inside a project built with the trim and AOT analysers
///     on — which the test assembly, having turned them off, cannot say for itself.
/// </remarks>
public sealed class AppearanceTests {
    [Fact]
    public void AFreshDesktopIsLightWithTheDeclaredAccent() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var appearance = new AppearanceSettings(store);

        Assert.Equal(ColorScheme.Light, appearance.Scheme);
        Assert.Equal("#0a84ff", appearance.AccentColor);
        Assert.False(appearance.ReduceMotion);
        Assert.False(appearance.IncreaseContrast);
    }

    [Fact]
    public void FollowingTheAccentColourIsOneSubscription() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var appearance = new AppearanceSettings(store);

        string? followed = null;
        using var subscription = appearance.Subscribe(settings => followed = settings.AccentColor);

        appearance.BeginEdit().SetAccentColor("#ff375f").SetScheme(ColorScheme.Dark).Commit();

        Assert.Equal("#ff375f", followed);
        Assert.Equal(ColorScheme.Dark, appearance.Scheme);
    }

    /// <summary>
    ///     Doc 08's "reset this pane", which is the same transaction as everything else.
    /// </summary>
    [Fact]
    public void ResetAllRestoresEveryDeclaredDefaultInOneCommit() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var appearance = new AppearanceSettings(store);

        appearance.BeginEdit()
            .SetScheme(ColorScheme.Dark)
            .SetAccentColor("#ff375f")
            .SetReduceMotion(true)
            .SetIncreaseContrast(true)
            .Commit();

        var events = 0;
        using var subscription = appearance.Subscribe(_ => events++);

        appearance.BeginEdit().ResetAll().Commit();

        Assert.Equal(1, events);
        Assert.Equal(ColorScheme.Light, appearance.Scheme);
        Assert.Equal("#0a84ff", appearance.AccentColor);
        Assert.False(appearance.ReduceMotion);
        Assert.False(appearance.IncreaseContrast);
    }

    /// <summary>
    ///     A managed profile that pins the appearance, and a pane that can say so.
    /// </summary>
    /// <remarks>
    ///     ⚠ The resolution carries the layer, not only the value, precisely so that doc 08's
    ///     control can be rendered disabled with an explanation rather than accepting an edit
    ///     that will not stick.
    /// </remarks>
    [Fact]
    public void AManagedProfileCanPinTheSchemeAndSayThatItDid() {
        using var temporary = new TemporaryLayout();
        temporary.Seed(SettingsLayer.ManagedProfile, AppearanceSettings.Schema,
            AppearanceSettings.Keys.Scheme, SettingValue.EnumMember("Dark"));

        using var store = temporary.Open();
        var appearance = new AppearanceSettings(store);

        appearance.BeginEdit().SetScheme(ColorScheme.Light).Commit();

        Assert.Equal(ColorScheme.Dark, appearance.Scheme);
        Assert.Equal(SettingsLayer.ManagedProfile, appearance.ResolveScheme().Layer);

        // The accent colour is not pinned, so the same pane's other control is still the
        // user's. One schema, two keys, two different answers to "may I change this".
        Assert.Null(appearance.ResolveAccentColor().Layer);
    }

    [Fact]
    public void TwoSchemasShareOneStoreWithoutSharingKeys() {
        using var temporary = new TemporaryLayout();
        using var store = temporary.Open();
        var appearance = new AppearanceSettings(store);
        var sample = new SampleSettings(store);

        appearance.BeginEdit().SetAccentColor("#ff375f").Commit();
        sample.BeginEdit().SetLabel("#ff375f").Commit();

        Assert.Equal(2, store.Schemas.Count);
        Assert.Equal("#ff375f", appearance.AccentColor);
        Assert.Equal("#ff375f", sample.Label);

        appearance.BeginEdit().ResetAll().Commit();
        Assert.Equal("#0a84ff", appearance.AccentColor);
        Assert.Equal("#ff375f", sample.Label);
    }
}

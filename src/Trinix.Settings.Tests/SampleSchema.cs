namespace Trinix.Settings.Tests;

/// <summary>Three members, one of them aliased, to pin the enum-by-name rule.</summary>
enum Corner {
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
}

/// <summary>
///     One key of every kind the store supports, declared through the real generator.
/// </summary>
/// <remarks>
///     ⚠ Declared here rather than reusing <c>IAppearanceSettings</c> because appearance has
///     no integer and no real, and a store whose Integer path is never exercised is a store
///     whose Integer path is never exercised. The generator runs over this assembly (see the
///     .csproj) so what these tests drive is the emitted code, not a hand-written stand-in.
/// </remarks>
[SettingsSchema("com.trinix.test.sample")]
interface ISampleSettings {
    [Setting(Default = true, Summary = "Whether the sample thing is on.")]
    bool Enabled { get; }

    [Setting(Default = 4, Summary = "How many sample things to keep.")]
    int Count { get; }

    [Setting(Default = 90L, Summary = "How long, in seconds, before a sample thing expires.")]
    long Timeout { get; }

    [Setting(Default = 0.5, Summary = "How opaque a sample thing is, from 0 to 1.")]
    double Opacity { get; }

    [Setting(Default = "sample", Summary = "What to call a sample thing.")]
    string Label { get; }

    [Setting(Default = Corner.TopLeft, Summary = "Which corner a sample thing starts in.")]
    Corner Origin { get; }
}

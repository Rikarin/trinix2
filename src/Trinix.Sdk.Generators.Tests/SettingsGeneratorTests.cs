namespace Trinix.Sdk.Generators.Tests;

/// <summary>
///     What the settings generator emits for a schema it accepts.
/// </summary>
/// <remarks>
///     ⚠ Every case here compiles the generated source against the real
///     <c>Trinix.Settings</c> — <see cref="GeneratorHarness" /> fails the run on any
///     compilation error — so these are not string-matching tests that would pass on output
///     which merely looks right. The string assertions are on top of that, for the handful of
///     decisions where "it compiles" is not the claim: which text an enum is stored under,
///     that the accessor inherits its declaration's accessibility, and that the whole schema
///     reaches the runtime descriptor doc 08 will index.
/// </remarks>
public sealed class SettingsGeneratorTests {
    const string Prologue = """
        using System;
        using Trinix.Settings;

        namespace Sample;

        """;

    const string Sample = """
        public enum Corner { TopLeft = 0, TopRight = 1 }

        [SettingsSchema("com.example.app")]
        public interface ISampleSettings {
            [Setting(Default = "sample", Summary = "What to call it.")]
            string Label { get; }

            [Setting(Default = 4, Summary = "How many to keep.")]
            int Count { get; }

            [Setting(Default = 90L, Summary = "How long to wait, in seconds.")]
            long Timeout { get; }

            [Setting(Default = 0.5, Summary = "How opaque it is.")]
            double Opacity { get; }

            [Setting(Default = true, Summary = "Whether it is on.")]
            bool Enabled { get; }

            [Setting(Default = Corner.TopRight, Summary = "Which corner it starts in.")]
            Corner Origin { get; }
        }
        """;

    static readonly GeneratorResult Generated = GeneratorHarness.RunSettings(Prologue + Sample);

    static string Accessor => Generated.File("SampleSettings.Settings.g.cs");

    [Fact]
    public void TheAccessorIsNamedAfterTheInterfaceWithoutItsI() =>
        Assert.Contains("public sealed class SampleSettings", Accessor, StringComparison.Ordinal);

    [Fact]
    public void EveryKeyGetsAConstantSoThatATypoIsACompileError() {
        foreach (var key in new[] { "Label", "Count", "Timeout", "Opacity", "Enabled", "Origin" }) {
            Assert.Contains($"SettingsKey {key} =>", Accessor, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The runtime schema is built from constants, with no reflection over the interface.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a style preference. <c>Trinix.Settings</c> builds with the trim and AOT
    ///     analysers on, and a settings binding that enumerated properties at startup would
    ///     fail the first <c>PublishAot</c> — in the compositor's build, not in the SDK's.
    /// </remarks>
    [Fact]
    public void TheRuntimeSchemaIsEmittedAsConstantsRatherThanDiscovered() {
        Assert.Contains("SettingValue.Text(\"sample\")", Accessor, StringComparison.Ordinal);
        Assert.Contains("SettingValue.Integer(4L)", Accessor, StringComparison.Ordinal);
        Assert.Contains("SettingValue.Real(0.5D)", Accessor, StringComparison.Ordinal);
        Assert.Contains("SettingValue.Bool(true)", Accessor, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(", Accessor, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperties", Accessor, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An enum's default and its setter both go through the member's name.
    /// </summary>
    /// <remarks>
    ///     ⚠ Storing the number instead would mean that renumbering an enum silently
    ///     repoints every user's stored value at a different member, and that a settings
    ///     database read on a serial console says <c>1</c> where it could say
    ///     <c>TopRight</c>.
    /// </remarks>
    [Fact]
    public void AnEnumIsStoredUnderItsMemberName() {
        Assert.Contains("SettingValue.EnumMember(\"TopRight\")", Accessor, StringComparison.Ordinal);
        Assert.Contains("\"TopLeft\" => global::Sample.Corner.TopLeft", Accessor, StringComparison.Ordinal);
        Assert.Contains("global::Sample.Corner.TopRight => \"TopRight\"", Accessor, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The reader falls back to the default; the writer throws. The asymmetry is the
    ///     point.
    /// </summary>
    /// <remarks>
    ///     A value going in came from this program, so an undefined one is a bug here. A
    ///     value coming out came from a file a person with <c>sqlite3</c> may have edited,
    ///     and a desktop that will not start because a preference says <c>Chartreuse</c> is
    ///     worse than one that is in the top left.
    /// </remarks>
    [Fact]
    public void AnUnknownEnumMemberFallsBackOnReadAndThrowsOnWrite() {
        Assert.Contains("_ => global::Sample.Corner.TopRight,", Accessor, StringComparison.Ordinal);
        Assert.Contains("_ => throw new global::System.ArgumentOutOfRangeException(", Accessor, StringComparison.Ordinal);
        Assert.DoesNotContain("Not a member of global::", Accessor, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKeyGetsASetterAndAReset() {
        Assert.Contains("public Edit SetLabel(string value)", Accessor, StringComparison.Ordinal);
        Assert.Contains("public Edit ResetLabel()", Accessor, StringComparison.Ordinal);
        Assert.Contains("public Edit ResetAll()", Accessor, StringComparison.Ordinal);
        Assert.Contains("public void Commit()", Accessor, StringComparison.Ordinal);
    }

    /// <summary>An int key reads back through a cast, because the store holds INTEGER.</summary>
    [Fact]
    public void AnIntKeyNarrowsFromTheStoredInteger() =>
        Assert.Contains("public int Count => (int)_store.Get(Keys.Count).AsInteger();", Accessor, StringComparison.Ordinal);

    [Fact]
    public void TheSummariesReachTheRuntimeDescriptorForDocEightsSearchIndex() {
        Assert.Contains("\"What to call it.\"", Accessor, StringComparison.Ordinal);
        Assert.Contains("\"Which corner it starts in.\"", Accessor, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An internal schema produces an internal accessor.
    /// </summary>
    /// <remarks>
    ///     ⚠ Always emitting <c>public</c> is what the first draft did, and it fails to
    ///     compile the moment an internal schema has an internal enum for a key: CS0053,
    ///     against a line of generated source, naming a property nobody wrote. Inheriting the
    ///     declaration's accessibility is also the obviously right thing — an application's
    ///     preferences are the application's.
    /// </remarks>
    [Fact]
    public void AnInternalSchemaProducesAnInternalAccessor() {
        var generated = GeneratorHarness.RunSettings(Prologue + """
            internal enum Corner { TopLeft = 0 }

            [SettingsSchema("com.example.app")]
            internal interface ISampleSettings {
                [Setting(Default = Corner.TopLeft, Summary = "Which corner it starts in.")]
                Corner Origin { get; }
            }
            """);

        Assert.Contains(
            "internal sealed class SampleSettings",
            generated.File("SampleSettings.Settings.g.cs"),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     The generator's identifier rule and the store's agree.
    /// </summary>
    /// <remarks>
    ///     ⚠ The rule is written twice — once in <c>TrinixSettingsGenerator</c> and once in
    ///     <c>Trinix.Settings.SettingsSchema</c> — and it has to be: this assembly targets
    ///     netstandard2.0 and is loaded into the compiler, so it cannot reference the one it
    ///     generates code for. This is the test that holds the two copies against each other,
    ///     which is the only thing that makes the duplication safe rather than merely
    ///     necessary.
    /// </remarks>
    [Theory]
    [InlineData("com.trinix.desktop.appearance")]
    [InlineData("io.trinix-2.app")]
    [InlineData("a.b")]
    [InlineData("")]
    [InlineData("appearance")]
    [InlineData("Com.Trinix.App")]
    [InlineData("com..trinix")]
    [InlineData(".com.trinix")]
    [InlineData("com.trinix.")]
    [InlineData("com.trin_ix")]
    public void TheGeneratorAndTheStoreAgreeAboutWhatASchemaIdentifierIs(string id) =>
        Assert.Equal(
            global::Trinix.Settings.SettingsSchema.IsWellFormedId(id),
            TrinixSettingsGenerator.IsWellFormedId(id)
        );
}

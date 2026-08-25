namespace Trinix.Sdk.Generators.Tests;

/// <summary>
///     The schemas the generator refuses, and doc 02's rules that are only rules because it
///     does.
/// </summary>
/// <remarks>
///     ⚠ Doc 02 § Settings storage says "a key with no schema is a bug the generator catches".
///     Nothing in the type system says so, and nothing in the store can say so either — a key
///     that was never declared is not a compile error, it is a preference reading its default
///     forever in a build that passes. These tests are the difference between that sentence
///     being a design document and being a constraint.
/// </remarks>
public sealed class SettingsRuleTests {
    const string Prologue = """
        using System;
        using System.Collections.Generic;
        using Trinix.Settings;

        namespace Sample;

        """;

    static IReadOnlyList<string> Diagnose(string body) =>
        GeneratorHarness.RunSettings(Prologue + body, expectFailure: true).Ids;

    /// <summary>Doc 02: a key with no schema is a bug the generator catches.</summary>
    [Fact]
    public void APropertyWithNoSettingAttributeIsRefused() =>
        Assert.Contains("TRX2002", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                string Label { get; }
            }
            """));

    [Fact]
    public void ASettingWithNoDefaultIsRefused() =>
        Assert.Contains("TRX2004", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Summary = "What to call it.")]
                string Label { get; }
            }
            """));

    [Fact]
    public void ASettingWhoseDefaultIsTheWrongTypeIsRefused() =>
        Assert.Contains("TRX2004", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = 4, Summary = "What to call it.")]
                string Label { get; }
            }
            """));

    /// <summary>
    ///     The rule that keeps the store from becoming a registry.
    /// </summary>
    /// <remarks>
    ///     ⚠ Doc 02's test for the boundary between a preference and application data is
    ///     whether it can be described in a schema, and the part of a schema that is hard to
    ///     fake is the description. A window position or a cached token has no sentence a
    ///     user would recognise, so writing one is a visible act rather than an omission.
    /// </remarks>
    [Fact]
    public void ASettingWithNoSummaryIsRefused() =>
        Assert.Contains("TRX2005", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = "sample")]
                string Label { get; }
            }
            """));

    [Fact]
    public void ASettingWithABlankSummaryIsRefused() =>
        Assert.Contains("TRX2005", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = "sample", Summary = "   ")]
                string Label { get; }
            }
            """));

    /// <summary>A collection is data, and data belongs in the container.</summary>
    [Fact]
    public void ACollectionKeyIsRefused() =>
        Assert.Contains("TRX2003", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = null, Summary = "The things.")]
                IReadOnlyList<string> Things { get; }
            }
            """));

    /// <summary>
    ///     A <c>[Flags]</c> enum has no member name for a combination, and settings store
    ///     enums by name.
    /// </summary>
    [Fact]
    public void AFlagsEnumIsRefused() =>
        Assert.Contains("TRX2003", Diagnose("""
            [Flags]
            public enum Options { None = 0, One = 1, Two = 2 }

            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = Options.One, Summary = "The options.")]
                Options Chosen { get; }
            }
            """));

    /// <summary>There is one kind of absence here, and it is the schema default.</summary>
    [Fact]
    public void ANullableKeyIsRefused() =>
        Assert.Contains("TRX2003", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = "sample", Summary = "What to call it.")]
                string? Label { get; }
            }
            """));

    /// <summary>An enum default that is a cast from an int has no member name to store.</summary>
    [Fact]
    public void AnEnumDefaultThatIsNotAMemberIsRefused() =>
        Assert.Contains("TRX2004", Diagnose("""
            public enum Corner { TopLeft = 0, TopRight = 1 }

            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = (Corner)7, Summary = "Which corner.")]
                Corner Origin { get; }
            }
            """));

    /// <summary>A setter would be a second way to write, one that skips the transaction.</summary>
    [Fact]
    public void ASettablePropertyIsRefused() =>
        Assert.Contains("TRX2006", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = "sample", Summary = "What to call it.")]
                string Label { get; set; }
            }
            """));

    /// <summary>A schema is a declaration; a method is behaviour.</summary>
    [Fact]
    public void AMethodOnASchemaIsRefused() =>
        Assert.Contains("TRX2006", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = "sample", Summary = "What to call it.")]
                string Label { get; }
                void Reset();
            }
            """));

    [Fact]
    public void AnIdentifierThatIsNotReverseDnsIsRefused() =>
        Assert.Contains("TRX2001", Diagnose("""
            [SettingsSchema("Appearance")]
            public interface ISample {
                [Setting(Default = "sample", Summary = "What to call it.")]
                string Label { get; }
            }
            """));

    [Fact]
    public void AGenericSchemaIsRefused() =>
        Assert.Contains("TRX2001", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample<T> {
                [Setting(Default = "sample", Summary = "What to call it.")]
                string Label { get; }
            }
            """));

    [Fact]
    public void ASchemaWithNoKeysIsRefused() =>
        Assert.Contains("TRX2001", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface ISample {
            }
            """));

    /// <summary>Two declarations of one identifier are two applications writing each other's keys.</summary>
    [Fact]
    public void TwoSchemasSharingAnIdentifierAreRefused() =>
        Assert.Contains("TRX2007", Diagnose("""
            [SettingsSchema("com.example.app")]
            public interface IFirst {
                [Setting(Default = "sample", Summary = "What to call it.")]
                string Label { get; }
            }

            [SettingsSchema("com.example.app")]
            public interface ISecond {
                [Setting(Default = 1, Summary = "How many.")]
                int Count { get; }
            }
            """));

    /// <summary>
    ///     A public schema cannot expose an internal enum, and the generator says so rather
    ///     than leaving CS0053 to point at a file that is not on disk.
    /// </summary>
    [Fact]
    public void APublicSchemaWithALessVisibleKeyTypeIsRefused() =>
        Assert.Contains("TRX2003", Diagnose("""
            internal enum Corner { TopLeft = 0 }

            [SettingsSchema("com.example.app")]
            public interface ISample {
                [Setting(Default = Corner.TopLeft, Summary = "Which corner.")]
                Corner Origin { get; }
            }
            """));
}

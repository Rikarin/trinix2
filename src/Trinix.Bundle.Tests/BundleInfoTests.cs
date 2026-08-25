using System.Text;
using System.Text.Json;

namespace Trinix.Bundle.Tests;

/// <summary>
///     <c>Info.json</c>'s validation rules and its round trip through the
///     source-generated serialiser.
/// </summary>
/// <remarks>
///     Two things are being pinned. The first is that
///     <see cref="BundleInfo.Validate" /> reports <i>every</i> problem rather than the
///     first — that is its stated contract, it exists so a developer packaging an
///     application spends one round trip instead of four, and it is the kind of
///     property a later refactor to early-return would quietly destroy. The second is
///     that the wire format is stable: these documents are signed by byte, so a
///     property name that changes is every bundle in the field failing verification.
/// </remarks>
public class BundleInfoTests {
    /// <summary>
    ///     A valid <c>Info.json</c>, with one field at a time replaceable.
    /// </summary>
    /// <remarks>
    ///     ⚠ A factory rather than a <c>with</c> expression: <see cref="BundleInfo" />
    ///     is a class, not a record, because it is a document rather than a value —
    ///     so there is no copy constructor to lean on.
    /// </remarks>
    static BundleInfo Info(
        string? schema = null,
        string identifier = "io.trinix.hello",
        string name = "Hello",
        string version = "1.0.0",
        string entryPoint = "Contents/Bin/hello",
        string? shortVersion = null,
        string? minimumSystemVersion = null,
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? categories = null
    ) => new() {
        Schema = schema ?? BundleSchema.Info,
        Identifier = identifier,
        Name = name,
        Version = version,
        EntryPoint = entryPoint,
        ShortVersion = shortVersion,
        MinimumSystemVersion = minimumSystemVersion,
        Permissions = permissions ?? [],
        Categories = categories ?? []
    };

    [Fact]
    public void AWellFormedInfoHasNoProblems() {
        Assert.Empty(Info().Validate());
    }

    [Fact]
    public void PermissionsAndCategoriesDefaultToEmptyRatherThanNull() {
        // The launcher enumerates both without a null check, and "declared no
        // permissions" and "the field was absent" must mean the same thing.
        var info = new BundleInfo {
            Identifier = "io.trinix.hello", Name = "Hello", Version = "1.0.0", EntryPoint = "Contents/Bin/hello"
        };
        Assert.Empty(info.Permissions);
        Assert.Empty(info.Categories);
    }

    [Theory]
    [InlineData("io.trinix.hello")]
    [InlineData("io.trinix")] // two labels is the minimum
    [InlineData("io.trinix.hello-world")]
    [InlineData("io.trinix.hello_world")]
    [InlineData("com.example.App2")]
    public void AcceptsAReverseDnsIdentifier(string identifier) {
        Assert.Empty(Info(identifier: identifier).Validate());
    }

    [Theory]
    [InlineData("hello")] // one label
    [InlineData("")]
    [InlineData("io..hello")] // empty label
    [InlineData(".io.hello")]
    [InlineData("io.hello.")]
    [InlineData("io.trinix.hello world")] // space
    [InlineData("io.trinix.héllo")] // non-ASCII: identity must be comparable byte for byte
    [InlineData("io.trinix.hello/../etc")]
    public void RejectsAnIdentifierThatIsNotReverseDns(string identifier) {
        var problems = Info(identifier: identifier).Validate();
        Assert.Contains(problems, p => p.Contains("reverse-DNS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0")]
    [InlineData("0.3.17.2")]
    public void AcceptsADottedNumericVersion(string version) {
        Assert.Empty(Info(version: version).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0.0-rc1")] // the ordered version is ordered; the pretty one is shortVersion
    [InlineData("v1.0")]
    [InlineData("1..0")]
    [InlineData("1.")]
    [InlineData("1.0.0 ")]
    public void RejectsAVersionThatIsNotDottedNumeric(string version) {
        var problems = Info(version: version).Validate();
        Assert.Contains(problems, p => p.Contains("dotted-numeric", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnEmptyName() {
        Assert.Contains("name is empty", Info(name: "   ").Validate());
    }

    [Fact]
    public void RejectsAnEntryPointThatCouldEscapeTheBundle() {
        var problems = Info(entryPoint: "../../bin/sh").Validate();
        Assert.Contains(problems, p => p.Contains("not a safe bundle-relative path", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnEntryPointOutsideContents() {
        // Safe as a path and still wrong: everything in a bundle lives under
        // Contents/, so an entry point elsewhere names a file the scanner never
        // saw and the signature therefore never covered.
        var problems = Info(entryPoint: "Bin/hello").Validate();
        Assert.Contains(problems, p => p.Contains("is outside Contents/", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsASchemaItDoesNotUnderstand() {
        var problems = Info(schema: "trinix.bundle/2").Validate();
        Assert.Contains(problems, p => p.Contains("expected 'trinix.bundle/1'", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAPermissionTrinixDoesNotDefine() {
        var problems = Info(permissions: ["files.home", "camera", "network.client"]).Validate();
        var problem = Assert.Single(problems);
        Assert.Contains("'camera'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsEveryPermissionItDefines() {
        // Guards the direction the vocabulary usually breaks in: a constant
        // added to BundlePermissions but forgotten in the backing array is a
        // permission the packager rejects and the documentation promises.
        Assert.Empty(Info(permissions: BundlePermissions.Known).Validate());
        Assert.All(BundlePermissions.Known, p => Assert.True(BundlePermissions.IsKnown(p)));
        Assert.False(BundlePermissions.IsKnown("camera"));
        Assert.False(BundlePermissions.IsKnown(""));
    }

    [Fact]
    public void ReportsEveryProblemAtOnce() {
        // The contract in Validate()'s own remarks. One round trip per mistake
        // is a bad way to spend an afternoon.
        var problems = Info(
            schema: "wrong",
            identifier: "hello",
            name: "",
            version: "v1",
            entryPoint: "../sh",
            permissions: ["camera"]
        ).Validate();

        Assert.Equal(6, problems.Count);
    }

    [Fact]
    public void RoundTripsThroughTheSourceGeneratedSerialiser() {
        var original = Info(
            shortVersion: "1.0 (beta)",
            minimumSystemVersion: "0.3",
            permissions: ["display", "network.client"],
            categories: ["utilities"]
        );

        var bytes = BundleJson.ToBytes(original, BundleJson.Default.BundleInfo);
        var parsed = JsonSerializer.Deserialize(bytes, BundleJson.Default.BundleInfo);

        Assert.NotNull(parsed);
        Assert.Equal(original.Identifier, parsed.Identifier);
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.ShortVersion, parsed.ShortVersion);
        Assert.Equal(original.MinimumSystemVersion, parsed.MinimumSystemVersion);
        Assert.Equal(original.EntryPoint, parsed.EntryPoint);
        Assert.Equal(original.Permissions, parsed.Permissions);
        Assert.Equal(original.Categories, parsed.Categories);
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void TheWireNamesAreTheOnesTheFormatDocuments() {
        // ⚠ These strings are load-bearing. Info.json is read by the launcher,
        // by the packaging tool and by PowerShell, and a renamed property is not
        // a refactor — it is a format change that every already-signed bundle
        // predates.
        var json = Encoding.UTF8.GetString(
            BundleJson.ToBytes(Info(minimumSystemVersion: "0.3"), BundleJson.Default.BundleInfo)
        );

        Assert.Contains("\"schema\":", json, StringComparison.Ordinal);
        Assert.Contains("\"identifier\":", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":", json, StringComparison.Ordinal);
        Assert.Contains("\"version\":", json, StringComparison.Ordinal);
        Assert.Contains("\"entryPoint\":", json, StringComparison.Ordinal);
        Assert.Contains("\"minimumSystemVersion\":", json, StringComparison.Ordinal);
        Assert.Contains("\"permissions\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NullPropertiesAreOmittedRatherThanWrittenAsNull() {
        var json = Encoding.UTF8.GetString(BundleJson.ToBytes(Info(), BundleJson.Default.BundleInfo));

        Assert.DoesNotContain("shortVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("minimumSystemVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerialisedDocumentsEndInExactlyOneNewline() {
        // Not decoration: the bytes written are the bytes signed, and an editor
        // or a `cat` that adds the missing newline would break the signature of
        // a bundle nobody knowingly touched.
        var bytes = BundleJson.ToBytes(Info(), BundleJson.Default.BundleInfo);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    [Fact]
    public void AnInfoJsonThatOmitsPermissionsValidatesRatherThanCrashing() {
        // ⚠ The regression this file was written and immediately earned its
        // keep on. System.Text.Json does not run field initialisers for a type
        // with required members, so "permissions": absent used to deserialise
        // to null — and Validate() then threw NullReferenceException out of
        // BundleVerifier for the most ordinary bundle there is: one that asks
        // for nothing. A launcher that crashes is worse than one that refuses.
        var parsed = JsonSerializer.Deserialize(
            """
            {
              "schema": "trinix.bundle/1",
              "identifier": "io.trinix.hello",
              "name": "Hello",
              "version": "1.0.0",
              "entryPoint": "Contents/Bin/hello"
            }
            """u8,
            BundleJson.Default.BundleInfo
        );

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Permissions);
        Assert.Empty(parsed.Categories);
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void AnExplicitJsonNullIsAlsoEmptyRatherThanNull() {
        var parsed = JsonSerializer.Deserialize(
            """
            {
              "schema": "trinix.bundle/1",
              "identifier": "io.trinix.hello",
              "name": "Hello",
              "version": "1.0.0",
              "entryPoint": "Contents/Bin/hello",
              "permissions": null,
              "categories": null
            }
            """u8,
            BundleJson.Default.BundleInfo
        );

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Permissions);
        Assert.Empty(parsed.Categories);
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void SerialisingRejectsANullTypeInfo() {
        Assert.Throws<ArgumentNullException>(() => BundleJson.ToBytes(Info(), null!));
    }
}

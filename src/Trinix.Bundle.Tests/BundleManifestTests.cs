using System.Text;
using System.Text.Json;

namespace Trinix.Bundle.Tests;

/// <summary>
///     The signed document itself: what it records, and what it refuses to record.
/// </summary>
/// <remarks>
///     The signature covers this file's <i>bytes</i>, which makes its serialised form
///     part of the format rather than an implementation detail. Every assertion about a
///     property name below is really an assertion that a bundle signed by Trinix 0.3
///     still verifies on Trinix 0.4 — the kind of compatibility that is free to keep
///     and impossible to recover once broken.
/// </remarks>
public class BundleManifestTests {
    static BundleManifest Manifest(params ManifestEntry[] entries) => new() {
        Identifier = "io.trinix.hello",
        Version = "1.0.0",
        Architecture = "arm64",
        SignedAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
        MerkleRoot = new string('0', 64),
        Entries = entries
    };

    static ManifestEntry Entry(string path, string content, bool executable = false) => new() {
        Path = path,
        Size = Encoding.UTF8.GetByteCount(content),
        Executable = executable,
        Sha256 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))
        )
    };

    [Fact]
    public void ComputingTheRootAgreesWithTheTreeItIsBuiltFrom() {
        var manifest = Manifest(Entry("Contents/Bin/hello", "x", executable: true), Entry("Contents/Info.json", "{}"));

        var expected = MerkleTree.Root(
            [
                MerkleTree.Leaf(
                    "Contents/Bin/hello",
                    1,
                    true,
                    System.Security.Cryptography.SHA256.HashData("x"u8)
                ),
                MerkleTree.Leaf(
                    "Contents/Info.json",
                    2,
                    false,
                    System.Security.Cryptography.SHA256.HashData("{}"u8)
                )
            ]
        );

        Assert.Equal(expected, manifest.ComputeMerkleRoot());
    }

    [Fact]
    public void AChangedEntryHashChangesTheComputedRoot() {
        var before = Manifest(Entry("Contents/Resources/data", "hello")).ComputeMerkleRoot();
        var after = Manifest(Entry("Contents/Resources/data", "hellp")).ComputeMerkleRoot();

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AManifestWithNoEntriesHasNoRoot() {
        Assert.Throws<ArgumentException>(() => Manifest().ComputeMerkleRoot());
    }

    [Fact]
    public void AnEntryWhoseHashIsNotHexIsAFormatErrorRatherThanACrash() {
        // The verifier catches FormatException here specifically, and turns it
        // into ContentMismatch. It can only do that if this throws it.
        var manifest = Manifest(
            new ManifestEntry { Path = "Contents/Info.json", Size = 2, Executable = false, Sha256 = "not hex" }
        );

        Assert.Throws<FormatException>(() => manifest.ComputeMerkleRoot());
    }

    [Fact]
    public void TheManifestRoundTripsThroughItsOwnSerialiser() {
        var original = Manifest(
            Entry("Contents/Bin/hello", "#!/bin/sh\n", executable: true),
            Entry("Contents/Info.json", "{}")
        );

        var parsed = JsonSerializer.Deserialize(
            BundleJson.ToBytes(original, BundleJson.Default.BundleManifest),
            BundleJson.Default.BundleManifest
        );

        Assert.NotNull(parsed);
        Assert.Equal(original.Schema, parsed.Schema);
        Assert.Equal(original.Identifier, parsed.Identifier);
        Assert.Equal(original.Architecture, parsed.Architecture);
        Assert.Equal(original.SignedAt, parsed.SignedAt);
        Assert.Equal(original.Entries.Count, parsed.Entries.Count);
        Assert.Equal(original.ComputeMerkleRoot(), parsed.ComputeMerkleRoot());
    }

    [Fact]
    public void TheSignedAtInstantSurvivesTheRoundTripAsAnInstantAndNotALocalTime() {
        // ⚠ An offset lost in serialisation would move the signing time by
        // however many hours the build machine is from UTC, and the field is
        // inside the signature — so the loss would be silent and permanent.
        var original = Manifest(Entry("Contents/Info.json", "{}"));

        var parsed = JsonSerializer.Deserialize(
            BundleJson.ToBytes(original, BundleJson.Default.BundleManifest),
            BundleJson.Default.BundleManifest
        )!;

        Assert.Equal(original.SignedAt.ToUniversalTime(), parsed.SignedAt.ToUniversalTime());
        Assert.Equal(TimeSpan.Zero, parsed.SignedAt.Offset);
    }

    [Fact]
    public void TheWireNamesAreTheOnesTheFormatDocuments() {
        var json = Encoding.UTF8.GetString(
            BundleJson.ToBytes(Manifest(Entry("Contents/Info.json", "{}")), BundleJson.Default.BundleManifest)
        );

        foreach (var name in new[] {
                     "schema", "identifier", "version", "architecture", "signedAt", "merkleRoot", "entries",
                     "path", "size", "executable", "sha256"
                 }) {
            Assert.Contains($"\"{name}\":", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSchemaMarkersAreTheOnesEveryReaderChecks() {
        Assert.Equal("trinix.bundle/1", BundleSchema.Info);
        Assert.Equal("trinix.signature/1", BundleSchema.Manifest);
        Assert.Equal("trinix.receipt/1", BundleSchema.Receipt);
    }

    [Fact]
    public void AnInstallReceiptRoundTrips() {
        // Not a trust anchor — nothing in the launch path believes it — but it
        // is what `trinix-bundle list` reads, so it has to survive the trip.
        var receipt = new InstallReceipt {
            Identifier = "io.trinix.hello",
            Version = "1.0.0",
            Path = "/Applications/Hello.app",
            MerkleRoot = new string('a', 64),
            SignerThumbprint = new string('b', 64),
            SignerSubject = "CN=A Developer, O=Trinix",
            InstalledAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
            FsVerity = new() { Enabled = 3, Skipped = 1, Reason = "filesystem does not support fs-verity" }
        };

        var parsed = JsonSerializer.Deserialize(
            BundleJson.ToBytes(receipt, BundleJson.Default.InstallReceipt),
            BundleJson.Default.InstallReceipt
        );

        Assert.NotNull(parsed);
        Assert.Equal(BundleSchema.Receipt, parsed.Schema);
        Assert.Equal(receipt.Identifier, parsed.Identifier);
        Assert.Equal(receipt.Path, parsed.Path);
        Assert.Equal(receipt.MerkleRoot, parsed.MerkleRoot);
        Assert.Equal(receipt.SignerSubject, parsed.SignerSubject);
        Assert.Equal(receipt.InstalledAt, parsed.InstalledAt);
        Assert.NotNull(parsed.FsVerity);
        Assert.Equal(3, parsed.FsVerity.Enabled);
        Assert.Equal(1, parsed.FsVerity.Skipped);
        Assert.Equal(receipt.FsVerity.Reason, parsed.FsVerity.Reason);
    }

    [Fact]
    public void AReceiptWithoutAnFsVerityReportIsStillAReceipt() {
        var receipt = new InstallReceipt {
            Identifier = "io.trinix.hello",
            Version = "1.0.0",
            Path = "/Applications/Hello.app",
            MerkleRoot = new string('a', 64),
            SignerThumbprint = new string('b', 64),
            SignerSubject = "CN=A Developer, O=Trinix",
            InstalledAt = DateTimeOffset.UtcNow
        };

        var parsed = JsonSerializer.Deserialize(
            BundleJson.ToBytes(receipt, BundleJson.Default.InstallReceipt),
            BundleJson.Default.InstallReceipt
        );

        Assert.NotNull(parsed);
        Assert.Null(parsed.FsVerity);
    }

    [Fact]
    public void EveryBundleFailureHasADistinctValueAndNoneIsZero() {
        // ⚠ The launcher's exit code is derived from this enum, so a
        // reordering is an interface change. None must stay 0 for the same
        // reason: "nothing went wrong" is the default of a default.
        var values = Enum.GetValues<BundleFailure>();

        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Equal(0, (int)BundleFailure.None);
        Assert.All(values.Where(v => v != BundleFailure.None), v => Assert.NotEqual(0, (int)v));
    }

    [Fact]
    public void ABundleExceptionCarriesItsReason() {
        var e = new BundleException(BundleFailure.UntrustedSigner, "nope");

        Assert.Equal(BundleFailure.UntrustedSigner, e.Failure);
        Assert.Equal("nope", e.Message);
        Assert.Equal(BundleFailure.None, new BundleException("no reason given").Failure);
    }
}

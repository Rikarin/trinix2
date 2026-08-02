using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Trinix.Bundle;

/// <summary>Format markers, so that a change of shape is never mistaken for corruption.</summary>
public static class BundleSchema
{
    /// <summary><c>Contents/Info.json</c>.</summary>
    public const string Info = "trinix.bundle/1";

    /// <summary><c>Contents/_Signature/manifest.json</c>.</summary>
    public const string Manifest = "trinix.signature/1";

    /// <summary>The receipt written when a bundle is installed.</summary>
    public const string Receipt = "trinix.receipt/1";
}

/// <summary>One file, as the manifest records it.</summary>
public sealed class ManifestEntry
{
    /// <summary>Bundle-relative, forward slashes, e.g. <c>Contents/Bin/hello</c>.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>Length in bytes.</summary>
    [JsonPropertyName("size")]
    public required long Size { get; init; }

    /// <summary>Whether the owner's execute bit is set. The only mode bit that changes behaviour.</summary>
    [JsonPropertyName("executable")]
    public required bool Executable { get; init; }

    /// <summary>SHA-256 of the file's contents, lowercase hex.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// <c>Contents/_Signature/manifest.json</c> — the document that is actually signed.
/// </summary>
/// <remarks>
/// <para>
/// The signature covers this file's bytes and nothing else, which is the whole
/// reason the file exists. Signing a directory means agreeing on how to
/// serialise a directory, and every format that has tried has produced a
/// canonicalisation bug. Here the signed object is a byte string that is
/// written once and read back literally; everything else in the bundle is
/// bound to it through hashes recorded in it.
/// </para>
/// <para>
/// It follows that verification has an order that must not be rearranged:
/// check the chain, check the signature over these exact bytes, and only then
/// believe a single field in the parsed object.
/// </para>
/// </remarks>
public sealed class BundleManifest
{
    /// <summary>Format marker.</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = BundleSchema.Manifest;

    /// <summary>Copied from <c>Info.json</c>, so identity is inside the signature.</summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>Copied from <c>Info.json</c>, for the same reason.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>
    /// The architecture this bundle's executables are built for
    /// (<c>arm64</c>, <c>x86_64</c>), or <c>any</c> for a bundle with no
    /// native code.
    /// </summary>
    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    /// <summary>
    /// When it was signed, as claimed by the signer.
    /// </summary>
    /// <remarks>
    /// Inside the signature rather than beside it. An unauthenticated signing
    /// time is worse than none: it looks like evidence and is not. This one is
    /// still only the signer's claim — a timestamping authority is what would
    /// make it more, and Trinix has no use for one until certificates start
    /// expiring.
    /// </remarks>
    [JsonPropertyName("signedAt")]
    public required DateTimeOffset SignedAt { get; init; }

    /// <summary>Root of the Merkle tree over <see cref="Entries"/>, lowercase hex.</summary>
    [JsonPropertyName("merkleRoot")]
    public required string MerkleRoot { get; init; }

    /// <summary>
    /// Every file in the bundle except the signature material itself, ordered
    /// ordinally by path. The order is part of the Merkle root.
    /// </summary>
    [JsonPropertyName("entries")]
    public required IReadOnlyList<ManifestEntry> Entries { get; init; }

    /// <summary>
    /// Recompute the Merkle root from <see cref="Entries"/>.
    /// </summary>
    /// <remarks>
    /// Used both when sealing (to fill the field) and when verifying (to catch
    /// a manifest whose recorded root disagrees with its own file list — which
    /// a signer could produce by accident and an attacker could not produce at
    /// all, but checking is a line of code and believing is a hole).
    /// </remarks>
    public byte[] ComputeMerkleRoot()
    {
        var leaves = new List<byte[]>(Entries.Count);
        foreach (ManifestEntry entry in Entries)
        {
            leaves.Add(MerkleTree.Leaf(entry.Path, entry.Size, entry.Executable, Convert.FromHexString(entry.Sha256)));
        }
        return MerkleTree.Root(leaves);
    }
}

/// <summary>
/// <c>/var/lib/trinix/bundles/&lt;identifier&gt;.json</c> — what the installer
/// recorded about a bundle it put on disk.
/// </summary>
/// <remarks>
/// Not a trust anchor. Nothing in the launch path believes this file; it exists
/// so that <c>trinix-bundle list</c> can answer without walking
/// <c>/Applications</c>, and so that a future package manager knows which
/// distribution image an installed application came from.
/// </remarks>
public sealed class InstallReceipt
{
    /// <summary>Format marker.</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = BundleSchema.Receipt;

    /// <summary>The application's identity.</summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>Its version at install time.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>Where the bundle was installed.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>The Merkle root that was verified.</summary>
    [JsonPropertyName("merkleRoot")]
    public required string MerkleRoot { get; init; }

    /// <summary>SHA-256 of the signer's certificate, lowercase hex.</summary>
    [JsonPropertyName("signerThumbprint")]
    public required string SignerThumbprint { get; init; }

    /// <summary>The signer's subject, for display.</summary>
    [JsonPropertyName("signerSubject")]
    public required string SignerSubject { get; init; }

    /// <summary>When the install happened.</summary>
    [JsonPropertyName("installedAt")]
    public required DateTimeOffset InstalledAt { get; init; }

    /// <summary>
    /// How many of the bundle's files fs-verity was enabled on, and why not
    /// more.
    /// </summary>
    [JsonPropertyName("fsVerity")]
    public FsVerityReport? FsVerity { get; init; }
}

/// <summary>What happened when the installer tried to enable fs-verity.</summary>
public sealed class FsVerityReport
{
    /// <summary>Number of files now protected by the kernel.</summary>
    [JsonPropertyName("enabled")]
    public required int Enabled { get; init; }

    /// <summary>Number it could not enable.</summary>
    [JsonPropertyName("skipped")]
    public required int Skipped { get; init; }

    /// <summary>The reason the first failure gave, if any.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Hex helpers, kept in one place so the casing is decided once.</summary>
internal static class Hex
{
    internal static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    internal static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, useAsync: true);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}

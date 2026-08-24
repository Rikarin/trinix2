using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Trinix.Bundle;

/// <summary>
///     Turns a directory into a signed bundle.
/// </summary>
/// <remarks>
///     The counterpart to <see cref="BundleVerifier" />, and the two have to agree
///     exactly — which is why the file list, the leaf encoding and the ordering all
///     live in one place each (<see cref="BundleScanner" />,
///     <see cref="MerkleTree" />, <see cref="BundleManifest.ComputeMerkleRoot" />)
///     and are called from both sides rather than written twice.
/// </remarks>
public static class BundleSealer {
    /// <summary>Trinix's signature algorithm: ECDSA over SHA-256, DER-encoded.</summary>
    /// <remarks>
    ///     <c>Rfc3279DerSequence</c> rather than the fixed-width IEEE P1363 form,
    ///     because DER is what X.509 and every command-line tool speak — a
    ///     signature that <c>openssl dgst -verify</c> can check is worth the four
    ///     extra bytes when something has gone wrong at three in the morning.
    /// </remarks>
    public const DSASignatureFormat SignatureFormat = DSASignatureFormat.Rfc3279DerSequence;

    /// <summary>
    ///     Sign the bundle at <paramref name="bundlePath" /> in place.
    /// </summary>
    /// <param name="bundlePath">A directory ending in <c>.app</c>.</param>
    /// <param name="identity">The signing certificate, with its private key.</param>
    /// <param name="architecture"><c>arm64</c>, <c>x86_64</c> or <c>any</c>.</param>
    /// <param name="signedAt">The signing time to record.</param>
    /// <param name="intermediates">
    ///     Certificates between the signer and the root, if the chain has any. The
    ///     root is deliberately not accepted: a bundle that carried its own trust
    ///     anchor would invite a verifier to use it.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The manifest that was written.</returns>
    public static async Task<BundleManifest> SealAsync(
        string bundlePath,
        X509Certificate2 identity,
        string architecture,
        DateTimeOffset signedAt,
        IReadOnlyList<X509Certificate2>? intermediates = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(bundlePath);
        ArgumentNullException.ThrowIfNull(identity);

        var info = await ReadInfoAsync(bundlePath, cancellationToken).ConfigureAwait(false);

        var problems = info.Validate();
        if (problems.Count > 0) {
            throw new BundleException(
                BundleFailure.MalformedInfo,
                $"{BundleLayout.InfoPath} is not valid: {string.Join("; ", problems)}"
            );
        }

        // Sealing twice must produce a bundle signed once. Left in place, the
        // old signature material would be excluded from the scan and silently
        // survive, so a bundle could carry a stale certificate alongside a
        // fresh manifest.
        var signatureDirectory = Path.Combine(bundlePath, BundleLayout.SignatureDirectory);
        if (Directory.Exists(signatureDirectory)) {
            Directory.Delete(signatureDirectory, true);
        }

        var files = BundleScanner.Scan(bundlePath);
        if (files.Count == 0) {
            throw new BundleException(BundleFailure.NotABundle, $"{bundlePath} contains no files to sign");
        }

        var entries = new List<ManifestEntry>(files.Count);
        var leaves = new List<byte[]>(files.Count);
        foreach (var file in files) {
            var hash = await Hex.HashFileAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            entries.Add(
                new() {
                    Path = file.RelativePath, Size = file.Size, Executable = file.Executable, Sha256 = Hex.Of(hash)
                }
            );
            leaves.Add(MerkleTree.Leaf(file.RelativePath, file.Size, file.Executable, hash));
        }

        // The entry point has to be in the bundle and has to be runnable. Both
        // are trivially true when a build produced the bundle and both are
        // wrong surprisingly often when a human assembled one by hand, and the
        // failure otherwise surfaces as an exec error at launch with no
        // indication that the bundle was always like that.
        var entryPoint = entries.Find(e => e.Path == info.EntryPoint);
        if (entryPoint is null) {
            throw new BundleException(
                BundleFailure.MalformedInfo,
                $"entryPoint '{info.EntryPoint}' does not exist in the bundle"
            );
        }

        if (!entryPoint.Executable) {
            throw new BundleException(
                BundleFailure.MalformedInfo,
                $"entryPoint '{info.EntryPoint}' is not executable"
            );
        }

        var manifest = new BundleManifest {
            Identifier = info.Identifier,
            Version = info.Version,
            Architecture = architecture,
            SignedAt = signedAt,
            MerkleRoot = Hex.Of(MerkleTree.Root(leaves)),
            Entries = entries
        };

        var manifestBytes = BundleJson.ToBytes(manifest, BundleJson.Default.BundleManifest);

        using var key = identity.GetECDsaPrivateKey();
        if (key is null) {
            throw new BundleException(
                BundleFailure.MalformedSignature,
                "the signing identity has no ECDSA private key"
            );
        }

        var signature = key.SignData(manifestBytes, HashAlgorithmName.SHA256, SignatureFormat);

        Directory.CreateDirectory(signatureDirectory);
        await File.WriteAllBytesAsync(
                Path.Combine(bundlePath, BundleLayout.ManifestPath),
                manifestBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                Path.Combine(bundlePath, BundleLayout.SignaturePath),
                signature,
                cancellationToken
            )
            .ConfigureAwait(false);

        var chain = new StringBuilder();
        chain.AppendLine(identity.ExportCertificatePem());
        foreach (var intermediate in intermediates ?? []) {
            chain.AppendLine(intermediate.ExportCertificatePem());
        }

        await File.WriteAllTextAsync(
                Path.Combine(bundlePath, BundleLayout.CertificatesPath),
                chain.ToString(),
                cancellationToken
            )
            .ConfigureAwait(false);

        return manifest;
    }

    /// <summary>Read and parse <c>Contents/Info.json</c>.</summary>
    public static async Task<BundleInfo> ReadInfoAsync(
        string bundlePath,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(bundlePath);

        var infoPath = Path.Combine(bundlePath, BundleLayout.InfoPath);
        if (!File.Exists(infoPath)) {
            throw new BundleException(
                BundleFailure.NotABundle,
                $"{bundlePath} has no {BundleLayout.InfoPath} — it is not an application bundle"
            );
        }

        var bytes = await File.ReadAllBytesAsync(infoPath, cancellationToken).ConfigureAwait(false);
        BundleInfo? info;
        try {
            info = JsonSerializer.Deserialize(bytes, BundleJson.Default.BundleInfo);
        } catch (JsonException e) {
            throw new BundleException(BundleFailure.MalformedInfo, $"{BundleLayout.InfoPath}: {e.Message}", e);
        }

        return info ?? throw new BundleException(BundleFailure.MalformedInfo, $"{BundleLayout.InfoPath} is empty");
    }
}

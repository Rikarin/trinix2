using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Trinix.Bundle;

/// <summary>What a verification concluded.</summary>
/// <param name="Failure">Why it failed, or <see cref="BundleFailure.None"/>.</param>
/// <param name="Message">One line, written for whoever has to act on it.</param>
/// <param name="Info">The bundle's identity, when it was readable.</param>
/// <param name="Manifest">The signed manifest, when the signature was valid.</param>
/// <param name="SignerSubject">The signer's distinguished name, when there was one.</param>
/// <param name="SignerThumbprint">SHA-256 of the signer's certificate, lowercase hex.</param>
/// <param name="AnchorSubject">The trusted root the chain terminated at.</param>
public readonly record struct VerificationResult(
    BundleFailure Failure,
    string Message,
    BundleInfo? Info,
    BundleManifest? Manifest,
    string? SignerSubject,
    string? SignerThumbprint,
    string? AnchorSubject)
{
    /// <summary>Did it pass?</summary>
    public bool Ok => Failure == BundleFailure.None;
}

/// <summary>
/// Decides whether a bundle may run.
/// </summary>
/// <remarks>
/// <para>
/// The order of the checks is the design. Each step may only use what the
/// previous step established:
/// </para>
/// <list type="number">
///   <item>the certificate chain terminates at a root the system image carries,
///         and the leaf is allowed to sign code;</item>
///   <item>the signature over the manifest's literal bytes verifies under that
///         leaf's public key — so from here on the manifest is the signer's
///         statement rather than the disk's;</item>
///   <item>the manifest's own Merkle root matches the file list it contains;</item>
///   <item>the bundle on disk is exactly that file list, byte for byte.</item>
/// </list>
/// <para>
/// Reversing any two of those produces a verifier that looks correct and is
/// not. Parsing the manifest before checking its signature, in particular,
/// means an attacker chooses what the verifier believes about the bundle it is
/// verifying.
/// </para>
/// </remarks>
public static class BundleVerifier
{
    /// <summary>
    /// Verify a bundle against a trust store.
    /// </summary>
    /// <param name="bundlePath">The <c>.app</c> directory.</param>
    /// <param name="trust">The roots to accept.</param>
    /// <param name="verificationTime">
    /// The instant to judge certificate validity at. Defaults to now.
    /// Deliberately not the manifest's <c>signedAt</c>: taking the time from
    /// the thing being verified would let an expired certificate stay usable
    /// forever by claiming an older signature.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<VerificationResult> VerifyAsync(
        string bundlePath,
        TrustStore trust,
        DateTimeOffset? verificationTime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);
        ArgumentNullException.ThrowIfNull(trust);

        BundleInfo info;
        try
        {
            info = await BundleSealer.ReadInfoAsync(bundlePath, cancellationToken).ConfigureAwait(false);
        }
        catch (BundleException e)
        {
            return Fail(e.Failure, e.Message);
        }

        IReadOnlyList<string> infoProblems = info.Validate();
        if (infoProblems.Count > 0)
        {
            return Fail(BundleFailure.MalformedInfo,
                $"{BundleLayout.InfoPath} is not valid: {string.Join("; ", infoProblems)}", info);
        }

        string manifestPath = Path.Combine(bundlePath, BundleLayout.ManifestPath);
        string signaturePath = Path.Combine(bundlePath, BundleLayout.SignaturePath);
        string certificatesPath = Path.Combine(bundlePath, BundleLayout.CertificatesPath);

        if (!File.Exists(manifestPath) || !File.Exists(signaturePath) || !File.Exists(certificatesPath))
        {
            return Fail(BundleFailure.NotSigned,
                $"{BundleLayout.NameOf(bundlePath)} is not signed — {BundleLayout.SignatureDirectory} is missing or incomplete",
                info);
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        byte[] signature = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);

        var chainCertificates = new X509Certificate2Collection();
        try
        {
            chainCertificates.ImportFromPemFile(certificatesPath);
        }
        catch (CryptographicException e)
        {
            return Fail(BundleFailure.MalformedSignature, $"{BundleLayout.CertificatesPath}: {e.Message}", info);
        }

        if (chainCertificates.Count == 0)
        {
            return Fail(BundleFailure.MalformedSignature,
                $"{BundleLayout.CertificatesPath} contains no certificates", info);
        }

        // Leaf first, by the format's definition. Anything else in the file is
        // an intermediate offered to the chain builder — offered, not trusted:
        // the builder still has to reach a root in the store.
        X509Certificate2 signer = chainCertificates[0];
        string signerThumbprint = Hex.Of(signer.GetCertHash(HashAlgorithmName.SHA256));

        // --- 1. Is the signer someone this system accepts? --------------------
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trust.Roots);
        chain.ChainPolicy.VerificationTime = (verificationTime ?? DateTimeOffset.UtcNow).UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(BundleLayout.CodeSigningEku));
        for (int i = 1; i < chainCertificates.Count; i++)
        {
            chain.ChainPolicy.ExtraStore.Add(chainCertificates[i]);
        }

        if (!chain.Build(signer))
        {
            string reasons = string.Join("; ", chain.ChainStatus.Select(s => s.Status.ToString()));
            return Fail(BundleFailure.UntrustedSigner,
                $"the signer '{signer.Subject}' does not chain to a root this system trusts ({reasons})",
                info, signerSubject: signer.Subject, signerThumbprint: signerThumbprint);
        }

        string anchor = chain.ChainElements[^1].Certificate.Subject;

        // Revocation is not checked, and that is a stated position rather than
        // an omission: there is no CRL distribution point and no OCSP responder
        // to reach, and on an image-based system the answer to a compromised
        // signing key is an update that replaces the trust store. Turning the
        // check on without infrastructure behind it would fail closed on a
        // machine with no network, which is most of them.

        // --- 2. Did that signer actually sign these bytes? --------------------
        using (ECDsa? publicKey = signer.GetECDsaPublicKey())
        {
            if (publicKey is null)
            {
                return Fail(BundleFailure.MalformedSignature,
                    $"the signer's certificate does not carry an ECDSA key", info,
                    signerSubject: signer.Subject, signerThumbprint: signerThumbprint, anchorSubject: anchor);
            }

            bool valid;
            try
            {
                valid = publicKey.VerifyData(
                    manifestBytes, signature, HashAlgorithmName.SHA256, BundleSealer.SignatureFormat);
            }
            catch (CryptographicException)
            {
                // A malformed DER signature throws rather than returning false.
                valid = false;
            }

            if (!valid)
            {
                return Fail(BundleFailure.BadSignature,
                    $"the signature over {BundleLayout.ManifestPath} is not valid — the manifest has been altered",
                    info, signerSubject: signer.Subject, signerThumbprint: signerThumbprint, anchorSubject: anchor);
            }
        }

        // From here the manifest is the signer's statement, and may be parsed.
        BundleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, BundleJson.Default.BundleManifest);
        }
        catch (JsonException e)
        {
            return Fail(BundleFailure.MalformedSignature, $"{BundleLayout.ManifestPath}: {e.Message}",
                info, signerSubject: signer.Subject, signerThumbprint: signerThumbprint, anchorSubject: anchor);
        }

        if (manifest is null || manifest.Schema != BundleSchema.Manifest)
        {
            return Fail(BundleFailure.MalformedSignature,
                $"{BundleLayout.ManifestPath} is not a {BundleSchema.Manifest} document",
                info, signerSubject: signer.Subject, signerThumbprint: signerThumbprint, anchorSubject: anchor);
        }

        VerificationResult Reject(BundleFailure failure, string message) =>
            new(failure, message, info, manifest, signer.Subject, signerThumbprint, anchor);

        // The identity in the signature must be the identity in Info.json.
        // Without this a signed manifest for one application could be dropped
        // into another whose Info.json claims to be something else entirely,
        // and every hash would still match.
        if (manifest.Identifier != info.Identifier || manifest.Version != info.Version)
        {
            return Reject(BundleFailure.ContentMismatch,
                $"the signature is for {manifest.Identifier} {manifest.Version}, but the bundle claims to be {info.Identifier} {info.Version}");
        }

        // --- 3. Is the manifest internally consistent? ------------------------
        foreach (ManifestEntry entry in manifest.Entries)
        {
            if (!BundleLayout.IsSafeRelativePath(entry.Path))
            {
                return Reject(BundleFailure.ContentMismatch,
                    $"the manifest names an unsafe path '{entry.Path}'");
            }
        }

        byte[] recomputedRoot;
        try
        {
            recomputedRoot = manifest.ComputeMerkleRoot();
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return Reject(BundleFailure.ContentMismatch, $"the manifest's file list is malformed: {e.Message}");
        }

        if (!Hex.Of(recomputedRoot).Equals(manifest.MerkleRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(BundleFailure.ContentMismatch,
                "the manifest's Merkle root does not match its own file list");
        }

        // --- 4. Is the bundle on disk that file list? -------------------------
        IReadOnlyList<ScannedFile> onDisk;
        try
        {
            onDisk = BundleScanner.Scan(bundlePath);
        }
        catch (BundleException e)
        {
            return Reject(e.Failure, e.Message);
        }

        if (onDisk.Count != manifest.Entries.Count)
        {
            var expected = manifest.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            var actual = onDisk.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

            string added = string.Join(", ", actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(3));
            string removed = string.Join(", ", expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(3));

            string detail = (added.Length, removed.Length) switch
            {
                (> 0, > 0) => $"added: {added}; missing: {removed}",
                (> 0, _) => $"added: {added}",
                (_, > 0) => $"missing: {removed}",
                _ => "the file list differs",
            };
            return Reject(BundleFailure.FileSetMismatch,
                $"the bundle does not contain the files it is signed for ({detail})");
        }

        for (int i = 0; i < onDisk.Count; i++)
        {
            ScannedFile file = onDisk[i];
            ManifestEntry entry = manifest.Entries[i];

            // Both lists are ordered ordinally by path, so a mismatch here is a
            // different file set rather than a different order.
            if (!string.Equals(file.RelativePath, entry.Path, StringComparison.Ordinal))
            {
                return Reject(BundleFailure.FileSetMismatch,
                    $"the bundle contains '{file.RelativePath}' where the signature expects '{entry.Path}'");
            }

            if (file.Size != entry.Size)
            {
                return Reject(BundleFailure.ContentMismatch,
                    $"'{entry.Path}' is {file.Size} bytes; the signature covers {entry.Size}");
            }

            if (file.Executable != entry.Executable)
            {
                return Reject(BundleFailure.ContentMismatch,
                    $"'{entry.Path}' is {(file.Executable ? "executable" : "not executable")}, which is not what was signed");
            }

            byte[] hash = await Hex.HashFileAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(entry.Sha256)))
            {
                return Reject(BundleFailure.ContentMismatch, $"'{entry.Path}' has been modified since it was signed");
            }
        }

        return new VerificationResult(
            BundleFailure.None,
            $"{info.Name} {info.Version} ({info.Identifier}) — signed by {signer.Subject}",
            info, manifest, signer.Subject, signerThumbprint, anchor);
    }

    private static VerificationResult Fail(
        BundleFailure failure,
        string message,
        BundleInfo? info = null,
        string? signerSubject = null,
        string? signerThumbprint = null,
        string? anchorSubject = null) =>
        new(failure, message, info, null, signerSubject, signerThumbprint, anchorSubject);
}

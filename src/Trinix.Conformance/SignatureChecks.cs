using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Trinix.Bundle;

namespace Trinix.Conformance;

/// <summary>
///     The signature, through <see cref="BundleVerifier" /> and not around it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here re-implements a verification step, and that is a rule
///         rather than a preference.</b> <see cref="BundleVerifier" />'s own remarks
///         explain that the order of its four checks is the design — chain, then
///         signature, then the manifest's self-consistency, then the disk — and that
///         reversing any two produces a verifier that looks correct and is not. A
///         conformance tool with a second, friendlier implementation of that order
///         would be a second chance to get it wrong, in the component whose whole
///         purpose is to be right. So doctor calls it and translates its answer.
///     </para>
///     <para>
///         The translation is the value added: <see cref="BundleFailure" /> is one
///         word, and what a developer needs is which of half a dozen quite different
///         mistakes produced it. A <see cref="BundleFailure.ContentMismatch" /> after a
///         successful seal almost always means the tree was touched afterwards, and
///         saying so is worth more than the enum name.
///     </para>
///     <para>
///         ⚠ The trust store is the awkward part. There is none on the machine that
///         builds bundles, and a check that cannot run must say so rather than pass —
///         see <see cref="SkippedCheck" />. What doctor will not do is invent an anchor
///         out of the bundle's own chain: certificates.pem deliberately excludes the
///         root, precisely so that nothing is tempted to verify a bundle against
///         something the bundle supplied.
///     </para>
/// </remarks>
static class SignatureChecks {
    internal static async Task RunAsync(
        DoctorContext context,
        BundleInfo info,
        string bundlePath,
        DoctorOptions options,
        CancellationToken cancellationToken
    ) {
        context.Ran("signature.sealed");

        var manifestPath = Path.Combine(bundlePath, BundleLayout.ManifestPath);
        if (!File.Exists(manifestPath)) {
            context.Warn(
                "signature.sealed",
                "the bundle is not sealed",
                "an unsealed bundle has no identity anything can check: Gatekeeper refuses it, "
                + "`trinix-bundle pack` refuses to wrap it, and the permissions it declares are a "
                + "suggestion rather than a signed statement",
                "run `trinix-bundle seal <bundle> --certificate … --key …`. Every check below needs "
                + "a signature and was skipped",
                BundleLayout.SignatureDirectory,
                storeGate: true
            );

            context.Skip("signature.verify", "the bundle is not sealed");
            context.Skip("signature.certificate", "the bundle is not sealed");
            return;
        }

        Certificate(context, bundlePath, options);
        await VerifyAsync(context, info, bundlePath, options, cancellationToken).ConfigureAwait(false);
    }

    static void Certificate(DoctorContext context, string bundlePath, DoctorOptions options) {
        context.Ran("signature.certificate");

        var certificatesPath = Path.Combine(bundlePath, BundleLayout.CertificatesPath);
        if (!File.Exists(certificatesPath)) {
            // contents.signature-material has already reported the missing file.
            context.Skip("signature.certificate", $"{BundleLayout.CertificatesPath} is missing");
            return;
        }

        var chain = new X509Certificate2Collection();
        try {
            chain.ImportFromPemFile(certificatesPath);
        } catch (CryptographicException e) {
            context.Error(
                "signature.certificate",
                $"{BundleLayout.CertificatesPath} cannot be read: {e.Message}",
                "the verifier reads the signer's certificate out of this file before it does "
                + "anything else, so a bundle it cannot parse is refused as malformed rather than "
                + "as untrusted",
                "re-seal the bundle; the sealer writes this file from the identity you pass it",
                BundleLayout.CertificatesPath
            );

            return;
        }

        if (chain.Count == 0) {
            context.Error(
                "signature.certificate",
                $"{BundleLayout.CertificatesPath} contains no certificates",
                "there is nothing to check the signature against, so the bundle is refused",
                "re-seal the bundle",
                BundleLayout.CertificatesPath
            );

            return;
        }

        using var signer = chain[0];
        var now = options.Now ?? DateTimeOffset.UtcNow;
        var expires = new DateTimeOffset(signer.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var days = (expires - now).TotalDays;

        if (days < 0) {
            context.Error(
                "signature.certificate",
                $"the signing certificate expired on {expires:yyyy-MM-dd} ({signer.Subject})",
                "certificate validity is judged at verification time and never at the time the "
                + "manifest claims it was signed — otherwise an expired key would stay usable "
                + "forever by backdating — so this bundle is already refused on every machine",
                "issue a new signing identity (`trinix-bundle pki identity …`) and re-seal",
                BundleLayout.CertificatesPath
            );

            return;
        }

        if (days < options.CertificateExpiryWarningDays) {
            context.Warn(
                "signature.certificate",
                $"the signing certificate expires on {expires:yyyy-MM-dd}, in "
                + ((int)days).ToString(CultureInfo.InvariantCulture) + " days",
                "a bundle signed today and shipped after that date verifies on your machine and is "
                + "refused on the user's, with a message about trust rather than about time",
                "issue a new signing identity before the next release, and re-seal",
                BundleLayout.CertificatesPath
            );
        }

        context.Ran("signature.code-signing-eku");
        var codeSigning = signer.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages
                .Cast<Oid>()
                .Any(oid => oid.Value == BundleLayout.CodeSigningEku));

        if (!codeSigning) {
            context.Error(
                "signature.code-signing-eku",
                $"the signing certificate does not carry the code-signing EKU ({BundleLayout.CodeSigningEku})",
                "the verifier requires it, which is what stops a TLS certificate from doubling as a "
                + "code-signing one — the chain will not build and the bundle reads as untrusted",
                "sign with an identity issued by `trinix-bundle pki identity`, which sets it",
                BundleLayout.CertificatesPath
            );
        }
    }

    static async Task VerifyAsync(
        DoctorContext context,
        BundleInfo info,
        string bundlePath,
        DoctorOptions options,
        CancellationToken cancellationToken
    ) {
        TrustStore trust;
        try {
            trust = TrustStore.Load(options.TrustDirectory);
        } catch (BundleException e) {
            context.Skip(
                "signature.verify",
                e.Message + " — pass --trust with the directory holding the root you signed against "
                + "(the development one is created by `trinix-bundle pki init`) to check the "
                + "signature, the Merkle root and the file list against the disk"
            );

            return;
        }

        context.Ran("signature.verify");

        var result = await BundleVerifier
            .VerifyAsync(bundlePath, trust, options.Now, cancellationToken)
            .ConfigureAwait(false);

        if (result.Ok) {
            context.Note(
                "signature.verify",
                $"verified: signed by {result.SignerSubject}, anchored at {result.AnchorSubject}",
                "the manifest's signature, its Merkle root and every file on disk agree — "
                + (result.Manifest is { } manifest
                    ? manifest.Entries.Count.ToString(CultureInfo.InvariantCulture) + " files, root "
                    + manifest.MerkleRoot[..Math.Min(16, manifest.MerkleRoot.Length)] + "…"
                    : "no manifest")
            );

            return;
        }

        context.Error(
            "signature.verify",
            result.Message,
            WhyRefused(result.Failure),
            FixFor(result.Failure, info),
            BundleLayout.SignatureDirectory
        );
    }

    static string WhyRefused(BundleFailure failure) =>
        failure switch {
            BundleFailure.UntrustedSigner =>
                "the chain has to end at a root in the trust store, which on a Trinix machine ships "
                + "inside the signed system image — this signature is fine and the signer is a "
                + "stranger",
            BundleFailure.BadSignature =>
                "the chain checked out and the signature over the manifest's bytes did not, which "
                + "means the manifest was altered after it was signed",
            BundleFailure.ContentMismatch or BundleFailure.FileSetMismatch =>
                "the signature is valid and covers a different bundle than the one on disk — after a "
                + "successful seal this almost always means the tree was touched afterwards",
            BundleFailure.MalformedSignature =>
                "the signature material is present and cannot be read as what it claims to be",
            BundleFailure.UnsupportedEntry =>
                "the bundle contains something that is not a regular file, so it cannot be hashed "
                + "into a manifest at all",
            BundleFailure.MalformedInfo =>
                "Info.json is refused before any signature is looked at; the findings above say why",
            BundleFailure.UnknownPermission =>
                "the manifest asks for authority this system has never heard of — the signature was "
                + "fine, the vocabulary was not",
            _ => "the verifier refuses this bundle, and a machine running Trinix will refuse it the "
                + "same way"
        };

    static string FixFor(BundleFailure failure, BundleInfo info) =>
        failure switch {
            BundleFailure.UntrustedSigner =>
                "sign with an identity that chains to a root in the trust store, or pass --trust "
                + "pointing at the root you did sign with",
            BundleFailure.BadSignature or BundleFailure.MalformedSignature =>
                "re-seal the bundle; do not edit anything under " + BundleLayout.SignatureDirectory,
            BundleFailure.ContentMismatch or BundleFailure.FileSetMismatch =>
                "re-seal after every change to the tree — sealing is the last step of a build, not "
                + "one you can do first. The message above names the files that differ",
            BundleFailure.UnsupportedEntry =>
                "remove the symbolic links reported under contents.symlink and seal again",
            BundleFailure.UnknownPermission =>
                "correct the permission strings in " + BundleLayout.InfoPath
                + " (this bundle declares: " + string.Join(", ", info.Permissions) + ")",
            _ => "fix the findings above and re-seal"
        };
}

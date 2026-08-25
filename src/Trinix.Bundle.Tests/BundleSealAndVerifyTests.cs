using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Trinix.Bundle.Tests;

/// <summary>
///     Seal a bundle, verify it, then break it in every way a bundle can be broken.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="BundleVerifier" />'s remarks describe four ordered checks —
///         chain, signature, manifest self-consistency, disk — and say that reversing
///         any two produces a verifier that looks correct and is not. The tests are
///         organised the same way, and each failure below is arranged to be caught by
///         exactly one of those steps, so the <see cref="BundleFailure" /> that comes
///         back is evidence about *which* step did the catching.
///     </para>
///     <para>
///         That distinction is not pedantry. The launcher shows a person one line, and
///         the difference between "this was tampered with" and "this was signed by
///         someone we do not know" is the difference between an incident and a
///         configuration mistake. A verifier that returned a single Boolean would pass
///         every test here and still be the wrong shape.
///     </para>
/// </remarks>
public class BundleSealAndVerifyTests {
    // ---------------------------------------------------------------------
    // The happy path, and what the manifest is supposed to contain.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ASealedBundleVerifies() {
        using var bundle = TestBundle.Create(nameof(ASealedBundleVerifies));
        await bundle.SealAsync();

        var result = await bundle.VerifyAsync();

        Assert.True(result.Ok, result.Message);
        Assert.Equal(BundleFailure.None, result.Failure);
        Assert.NotNull(result.Info);
        Assert.NotNull(result.Manifest);
        Assert.Equal("io.trinix.hello", result.Info.Identifier);
        Assert.Contains("Trinix Test Developer", result.SignerSubject!, StringComparison.Ordinal);
        Assert.Contains("Trinix Test Root", result.AnchorSubject!, StringComparison.Ordinal);
        Assert.Equal(64, result.SignerThumbprint!.Length); // SHA-256, lowercase hex
    }

    [Fact]
    public async Task VerificationHandsBackTheSignedManifestAndNotOnlyAVerdict() {
        // ⚠ The manifest is an output rather than an implementation detail, and the
        // caller that needs it is the launcher. Doc 04's sandbox decides one property
        // — MemoryDenyWriteExecute= — from whether the bundle carries a
        // *.runtimeconfig.json, and it has to read that out of the *signed* file list
        // rather than off the disk: a directory listing is whatever is there right
        // now, and the conclusion drawn from it would be exactly as trustworthy.
        // Narrowing this result to Ok/Failure would take that away with nothing else
        // failing, so it is asserted here rather than left to the one caller.
        using var bundle = TestBundle.Create(nameof(VerificationHandsBackTheSignedManifestAndNotOnlyAVerdict));
        var signedManifest = await bundle.SealAsync();

        var result = await bundle.VerifyAsync();

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.Manifest);
        Assert.Equal(signedManifest.MerkleRoot, result.Manifest.MerkleRoot);
        Assert.Equal(
            signedManifest.Entries.Select(entry => entry.Path),
            result.Manifest.Entries.Select(entry => entry.Path)
        );
    }

    [Fact]
    public async Task TheManifestCoversEveryFileExceptItsOwnSignature() {
        using var bundle = TestBundle.Create(nameof(TheManifestCoversEveryFileExceptItsOwnSignature));
        var manifest = await bundle.SealAsync();

        Assert.Equal(
            [
                "Contents/Bin/hello",
                "Contents/Info.json",
                "Contents/Resources/greeting.txt"
            ],
            manifest.Entries.Select(e => e.Path)
        );

        // A signature cannot cover itself, so the three files under
        // _Signature/ must be absent — and their absence is what makes the
        // exclusion rule load-bearing rather than cosmetic.
        Assert.DoesNotContain(manifest.Entries, e => BundleLayout.IsSignatureMaterial(e.Path));
        Assert.True(File.Exists(bundle.At(BundleLayout.ManifestPath)));
        Assert.True(File.Exists(bundle.At(BundleLayout.SignaturePath)));
        Assert.True(File.Exists(bundle.At(BundleLayout.CertificatesPath)));
    }

    [Fact]
    public async Task TheEntriesAreOrderedOrdinallyByPath() {
        // The order is an input to the Merkle root, so it has to be the walk's
        // property rather than the filesystem's. Files are written here in an
        // order that is neither sorted nor the order they will come back in.
        //
        // ⚠ The names differ by more than case. macOS is case-insensitive, so
        // "Apple" and "apple" would collide into one file there and stay two on
        // CI; 'Z' before '_' before 'a' separates ordinal from culture-aware
        // ordering without depending on the host filesystem at all.
        using var bundle = TestBundle.Create(nameof(TheEntriesAreOrderedOrdinallyByPath));
        bundle.WriteFile("Contents/Resources/zebra", "z");
        bundle.WriteFile("Contents/Resources/Zulu", "Z");
        bundle.WriteFile("Contents/Resources/_private", "_");

        var manifest = await bundle.SealAsync();
        var paths = manifest.Entries.Select(e => e.Path).ToArray();

        Assert.Equal(paths.Order(StringComparer.Ordinal).ToArray(), paths);
        Assert.True(
            Array.IndexOf(paths, "Contents/Resources/Zulu")
            < Array.IndexOf(paths, "Contents/Resources/_private")
            && Array.IndexOf(paths, "Contents/Resources/_private")
            < Array.IndexOf(paths, "Contents/Resources/zebra"),
            $"ordinal ordering puts Z before _ before z; got {string.Join(", ", paths)}"
        );
    }

    [Fact]
    public async Task TheRecordedRootIsTheRootOfTheRecordedFileList() {
        using var bundle = TestBundle.Create(nameof(TheRecordedRootIsTheRootOfTheRecordedFileList));
        var manifest = await bundle.SealAsync();

        Assert.Equal(manifest.MerkleRoot, Convert.ToHexStringLower(manifest.ComputeMerkleRoot()));
    }

    [Fact]
    public async Task TheEntriesRecordSizeAndTheExecutableBit() {
        using var bundle = TestBundle.Create(nameof(TheEntriesRecordSizeAndTheExecutableBit));
        var manifest = await bundle.SealAsync();

        var entry = manifest.Entries.Single(e => e.Path == "Contents/Bin/hello");
        Assert.Equal(new FileInfo(bundle.At("Contents/Bin/hello")).Length, entry.Size);
        Assert.True(entry.Executable);
        Assert.False(manifest.Entries.Single(e => e.Path == "Contents/Info.json").Executable);
    }

    [Fact]
    public async Task SealingTwiceLeavesABundleSignedOnce() {
        // The old signature material is excluded from the scan, so left in
        // place it would survive silently and the bundle would carry a stale
        // certificate beside a fresh manifest.
        using var bundle = TestBundle.Create(nameof(SealingTwiceLeavesABundleSignedOnce));
        await bundle.SealAsync();
        var certificatesAfterFirst = await File.ReadAllTextAsync(bundle.At(BundleLayout.CertificatesPath));

        bundle.WriteFile("Contents/Resources/extra.txt", "added between seals\n");
        var second = await bundle.SealAsync();

        Assert.Contains(second.Entries, e => e.Path == "Contents/Resources/extra.txt");
        Assert.True((await bundle.VerifyAsync()).Ok);

        // One certificate in the file, not two appended.
        var certificatesAfterSecond = await File.ReadAllTextAsync(bundle.At(BundleLayout.CertificatesPath));
        Assert.Equal(certificatesAfterFirst.Length, certificatesAfterSecond.Length);
        Assert.Equal(
            1,
            certificatesAfterSecond.Split("-----BEGIN CERTIFICATE-----", StringSplitOptions.None).Length - 1
        );
    }

    [Fact]
    public async Task TheSignatureIsCheckableWithAnOrdinaryEcdsaVerify() {
        // The point of DER-encoding the signature: a bundle that only
        // Trinix.Bundle can check is a bundle nobody can debug at 3am. This
        // repeats the verification without going through BundleVerifier at all.
        using var bundle = TestBundle.Create(nameof(TheSignatureIsCheckableWithAnOrdinaryEcdsaVerify));
        await bundle.SealAsync();

        var manifestBytes = await File.ReadAllBytesAsync(bundle.At(BundleLayout.ManifestPath));
        var signature = await File.ReadAllBytesAsync(bundle.At(BundleLayout.SignaturePath));

        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(bundle.At(BundleLayout.CertificatesPath));
        using var key = chain[0].GetECDsaPublicKey();

        Assert.NotNull(key);
        Assert.True(
            key.VerifyData(
                manifestBytes,
                signature,
                HashAlgorithmName.SHA256,
                BundleSealer.SignatureFormat
            )
        );
    }

    // ---------------------------------------------------------------------
    // Step 4: the bundle on disk is not the file list that was signed.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AFlippedByteInAResourceIsCaught() {
        // The property the whole format rests on, exercised end to end rather
        // than against MerkleTree in isolation.
        using var bundle = TestBundle.Create(nameof(AFlippedByteInAResourceIsCaught));
        await bundle.SealAsync();
        Assert.True((await bundle.VerifyAsync()).Ok);

        bundle.FlipAByte("Contents/Resources/greeting.txt");

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("has been modified since it was signed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlippedByteInTheExecutableIsCaught() {
        using var bundle = TestBundle.Create(nameof(AFlippedByteInTheExecutableIsCaught));
        await bundle.SealAsync();

        bundle.FlipAByte("Contents/Bin/hello");

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("Contents/Bin/hello", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWhoseLengthChangedIsCaughtBeforeItsHashIsComputed() {
        using var bundle = TestBundle.Create(nameof(AFileWhoseLengthChangedIsCaughtBeforeItsHashIsComputed));
        await bundle.SealAsync();

        await File.AppendAllTextAsync(bundle.At("Contents/Resources/greeting.txt"), "and more\n");

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("bytes; the signature covers", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakingADataFileExecutableIsCaught() {
        // Not a content change at all, which is why the mode bit is inside the
        // Merkle leaf rather than beside it.
        using var bundle = TestBundle.Create(nameof(MakingADataFileExecutableIsCaught));
        await bundle.SealAsync();

        var path = bundle.At("Contents/Resources/greeting.txt");
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("which is not what was signed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileAddedAfterSigningIsCaught() {
        using var bundle = TestBundle.Create(nameof(AFileAddedAfterSigningIsCaught));
        await bundle.SealAsync();

        bundle.WriteFile("Contents/Resources/smuggled.so", "not covered by the signature\n");

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.FileSetMismatch, result.Failure);
        Assert.Contains("added: Contents/Resources/smuggled.so", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileRemovedAfterSigningIsCaught() {
        using var bundle = TestBundle.Create(nameof(AFileRemovedAfterSigningIsCaught));
        await bundle.SealAsync();

        File.Delete(bundle.At("Contents/Resources/greeting.txt"));

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.FileSetMismatch, result.Failure);
        Assert.Contains("missing: Contents/Resources/greeting.txt", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileRenamedAfterSigningIsCaught() {
        // Same count, same bytes, different name — the case a naive "compare
        // the two lists by length" check would wave through.
        using var bundle = TestBundle.Create(nameof(AFileRenamedAfterSigningIsCaught));
        await bundle.SealAsync();

        File.Move(bundle.At("Contents/Resources/greeting.txt"), bundle.At("Contents/Resources/farewell.txt"));

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.FileSetMismatch, result.Failure);
        Assert.Contains("where the signature expects", result.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Step 2: the manifest is not what was signed.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AnEditedManifestIsCaughtBeforeItIsParsed() {
        // ⚠ The manifest here is edited to say something *true* about a
        // tampered bundle. If the verifier parsed before checking the
        // signature, this would be the attack: rewrite the file list, rewrite
        // the root, and the disk comparison passes. The signature check is what
        // makes the rewrite pointless, so it has to come first.
        using var bundle = TestBundle.Create(nameof(AnEditedManifestIsCaughtBeforeItIsParsed));
        await bundle.SealAsync();

        bundle.FlipAByte("Contents/Resources/greeting.txt");

        // A manifest that tells the truth about the *tampered* bundle: every
        // hash recomputed, the Merkle root recomputed to match. It is not
        // signed, and that is the only thing wrong with it.
        var entries = BundleScanner.Scan(bundle.Path)
            .Select(
                f => new ManifestEntry {
                    Path = f.RelativePath,
                    Size = f.Size,
                    Executable = f.Executable,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.FullPath)))
                }
            )
            .ToArray();

        var withoutRoot = TestBundle.Rewrite(await bundle.ReadManifestAsync(), entries: entries);
        var honest = TestBundle.Rewrite(
            withoutRoot,
            merkleRoot: Convert.ToHexStringLower(withoutRoot.ComputeMerkleRoot())
        );

        await File.WriteAllBytesAsync(
            bundle.At(BundleLayout.ManifestPath),
            BundleJson.ToBytes(honest, BundleJson.Default.BundleManifest)
        );

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.BadSignature, result.Failure);
        Assert.Contains("the manifest has been altered", result.Message, StringComparison.Ordinal);

        // Nothing was parsed out of the rewritten manifest.
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task AWhitespaceOnlyEditToTheManifestIsStillAnAlteration() {
        // The signature covers bytes, not meaning. A trailing newline added by
        // an editor is a broken bundle, and that is the intended behaviour —
        // it is why BundleJson.ToBytes writes the newline itself.
        using var bundle = TestBundle.Create(nameof(AWhitespaceOnlyEditToTheManifestIsStillAnAlteration));
        await bundle.SealAsync();

        var path = bundle.At(BundleLayout.ManifestPath);
        await File.AppendAllTextAsync(path, "\n");

        Assert.Equal(BundleFailure.BadSignature, (await bundle.VerifyAsync()).Failure);
    }

    [Fact]
    public async Task ACorruptSignatureFileIsRefusedRatherThanThrown() {
        // A malformed DER signature makes ECDsa.VerifyData throw rather than
        // return false, and an exception escaping the verifier is a crash where
        // a refusal belongs.
        using var bundle = TestBundle.Create(nameof(ACorruptSignatureFileIsRefusedRatherThanThrown));
        await bundle.SealAsync();

        await File.WriteAllBytesAsync(bundle.At(BundleLayout.SignaturePath), [0x30, 0x00, 0xff, 0xff]);

        Assert.Equal(BundleFailure.BadSignature, (await bundle.VerifyAsync()).Failure);
    }

    [Fact]
    public async Task AnEmptySignatureFileIsRefused() {
        using var bundle = TestBundle.Create(nameof(AnEmptySignatureFileIsRefused));
        await bundle.SealAsync();

        await File.WriteAllBytesAsync(bundle.At(BundleLayout.SignaturePath), []);

        Assert.Equal(BundleFailure.BadSignature, (await bundle.VerifyAsync()).Failure);
    }

    // ---------------------------------------------------------------------
    // Step 1: the signer is not someone this system accepts.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ABundleSignedUnderAnotherRootIsRefused() {
        // The signature is perfectly valid. It is the chain that is wrong, and
        // the message has to say so — this is the "someone else's developer
        // certificate" case, not the tampering case.
        using var bundle = TestBundle.Create(nameof(ABundleSignedUnderAnotherRootIsRefused));
        await bundle.SealAsync();

        using var otherRoot = DeveloperPki.CreateRoot("Someone Else", "Elsewhere", DateTimeOffset.UtcNow.AddDays(-1));
        var result = await BundleVerifier.VerifyAsync(bundle.Path, TrustStore.FromCertificates([otherRoot], "other"));

        Assert.Equal(BundleFailure.UntrustedSigner, result.Failure);
        Assert.Contains("does not chain to a root this system trusts", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.SignerThumbprint); // still worth reporting who it *was*
        Assert.Null(result.AnchorSubject);
    }

    [Fact]
    public async Task ABundleWhoseCertificateIsNotYetValidIsRefused() {
        // Verification time comes from the caller, never from the manifest's
        // signedAt — taking it from the thing being verified would let an
        // expired certificate stay usable forever by claiming an older
        // signature.
        var notBefore = DateTimeOffset.UtcNow;
        using var bundle = TestBundle.Create(nameof(ABundleWhoseCertificateIsNotYetValidIsRefused), notBefore: notBefore);
        await bundle.SealAsync();

        var result = await bundle.VerifyAsync(notBefore.AddDays(-30));

        Assert.Equal(BundleFailure.UntrustedSigner, result.Failure);
        Assert.Contains("NotTimeValid", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleWhoseCertificateHasExpiredIsRefused() {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var bundle = TestBundle.Create(nameof(ABundleWhoseCertificateHasExpiredIsRefused), notBefore: notBefore);
        await bundle.SealAsync();

        // Past the developer certificate's five years, inside the root's twenty.
        var result = await bundle.VerifyAsync(notBefore.AddYears(DeveloperPki.IdentityYears + 1));

        Assert.Equal(BundleFailure.UntrustedSigner, result.Failure);
        Assert.Contains("NotTimeValid", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyCertificateFileIsRefused() {
        using var bundle = TestBundle.Create(nameof(AnEmptyCertificateFileIsRefused));
        await bundle.SealAsync();

        await File.WriteAllTextAsync(bundle.At(BundleLayout.CertificatesPath), "# nothing here\n");

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.MalformedSignature, result.Failure);
        Assert.Contains("contains no certificates", result.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Before any of that: is this even a bundle, and is it signed?
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AnUnsealedBundleIsNotSignedRatherThanUntrusted() {
        using var bundle = TestBundle.Create(nameof(AnUnsealedBundleIsNotSignedRatherThanUntrusted));

        var result = await bundle.VerifyAsync();

        Assert.Equal(BundleFailure.NotSigned, result.Failure);
        Assert.NotNull(result.Info); // Info.json was readable; only the signature is missing
    }

    [Fact]
    public async Task APartiallySignedBundleIsNotSigned() {
        // All three files or none. A bundle with a manifest and no certificate
        // is not "nearly signed".
        using var bundle = TestBundle.Create(nameof(APartiallySignedBundleIsNotSigned));
        await bundle.SealAsync();
        File.Delete(bundle.At(BundleLayout.CertificatesPath));

        Assert.Equal(BundleFailure.NotSigned, (await bundle.VerifyAsync()).Failure);
    }

    [Fact]
    public async Task ADirectoryWithNoInfoJsonIsNotABundle() {
        using var scratch = TempDirectory.Create(nameof(ADirectoryWithNoInfoJsonIsNotABundle));
        using var root = DeveloperPki.CreateRoot("Trinix Test Root", "Trinix", DateTimeOffset.UtcNow.AddDays(-1));

        var result = await BundleVerifier.VerifyAsync(
            scratch.Combine("Empty.app"),
            TrustStore.FromCertificates([root], "test")
        );

        Assert.Equal(BundleFailure.NotABundle, result.Failure);
    }

    [Fact]
    public async Task AnUnparseableInfoJsonIsMalformedRatherThanMissing() {
        using var bundle = TestBundle.Create(nameof(AnUnparseableInfoJsonIsMalformedRatherThanMissing));
        await File.WriteAllTextAsync(bundle.At(BundleLayout.InfoPath), "{ this is not json");

        var result = await bundle.VerifyAsync();

        Assert.Equal(BundleFailure.MalformedInfo, result.Failure);
    }

    [Fact]
    public async Task AnInfoJsonThatFailsValidationIsRefusedBeforeAnyCryptography() {
        using var bundle = TestBundle.Create(
            nameof(AnInfoJsonThatFailsValidationIsRefusedBeforeAnyCryptography),
            identifier: "io.trinix.hello"
        );
        await bundle.SealAsync();

        // Rewrite Info.json to something that parses and does not validate.
        // Bad identity is a different answer from bad signature, even though
        // this edit also invalidates the manifest.
        await File.WriteAllTextAsync(
            bundle.At(BundleLayout.InfoPath),
            """
            {
              "schema": "trinix.bundle/1",
              "identifier": "hello",
              "name": "Hello",
              "version": "1.0.0",
              "entryPoint": "Contents/Bin/hello"
            }
            """
        );

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.MalformedInfo, result.Failure);
        Assert.Contains("reverse-DNS", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASignatureForAnotherApplicationIsRefused() {
        // ⚠ The manifest carries identifier and version so that identity is
        // inside the signature. Without that check, a signed manifest could be
        // dropped into a bundle whose Info.json claims to be something else and
        // every hash would still match.
        using var bundle = TestBundle.Create(nameof(ASignatureForAnotherApplicationIsRefused));
        await bundle.SealAsync();

        var impostor = TestBundle.Rewrite(await bundle.ReadManifestAsync(), identifier: "io.trinix.somethingelse");
        await bundle.ReSignAsync(impostor);

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("the signature is for io.trinix.somethingelse", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestNamingAnUnsafePathIsRefusedEvenThoughItIsSigned() {
        // ⚠ A valid signature proves a developer signed it, not that the
        // developer was careful. A path that escapes the bundle has to be
        // refused after the signature checks out, not instead of them.
        using var bundle = TestBundle.Create(nameof(AManifestNamingAnUnsafePathIsRefusedEvenThoughItIsSigned));
        await bundle.SealAsync();

        var original = await bundle.ReadManifestAsync();
        var entries = original.Entries.ToList();
        entries[0] = new() {
            Path = "../../etc/passwd", Size = entries[0].Size, Executable = false, Sha256 = entries[0].Sha256
        };

        await bundle.ReSignAsync(TestBundle.Rewrite(original, entries: entries));

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("unsafe path '../../etc/passwd'", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestWhoseRootDisagreesWithItsOwnFileListIsRefused() {
        // A signer could produce this by accident and an attacker could not
        // produce it at all — but checking is a line of code and believing is a
        // hole.
        using var bundle = TestBundle.Create(nameof(AManifestWhoseRootDisagreesWithItsOwnFileListIsRefused));
        await bundle.SealAsync();

        var manifest = await bundle.ReadManifestAsync();
        await bundle.ReSignAsync(TestBundle.Rewrite(manifest, merkleRoot: new string('0', 64)));

        var result = await bundle.VerifyAsync();
        Assert.Equal(BundleFailure.ContentMismatch, result.Failure);
        Assert.Contains("does not match its own file list", result.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Sealing refuses bundles that could never verify.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SealingRefusesAnInfoJsonThatDoesNotValidate() {
        using var bundle = TestBundle.Create(nameof(SealingRefusesAnInfoJsonThatDoesNotValidate));
        await File.WriteAllTextAsync(
            bundle.At(BundleLayout.InfoPath),
            """
            {
              "schema": "trinix.bundle/1",
              "identifier": "io.trinix.hello",
              "name": "Hello",
              "version": "not-a-version",
              "entryPoint": "Contents/Bin/hello"
            }
            """
        );

        var e = await Assert.ThrowsAsync<BundleException>(() => bundle.SealAsync());
        Assert.Equal(BundleFailure.MalformedInfo, e.Failure);
    }

    [Fact]
    public async Task SealingRefusesAnEntryPointThatIsNotInTheBundle() {
        // Trivially true when a build produced the bundle, wrong surprisingly
        // often when a human assembled one — and otherwise it surfaces as an
        // exec error at launch with no hint that the bundle was always like it.
        using var bundle = TestBundle.Create(nameof(SealingRefusesAnEntryPointThatIsNotInTheBundle));
        File.Delete(bundle.At("Contents/Bin/hello"));

        var e = await Assert.ThrowsAsync<BundleException>(() => bundle.SealAsync());
        Assert.Equal(BundleFailure.MalformedInfo, e.Failure);
        Assert.Contains("does not exist in the bundle", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SealingRefusesAnEntryPointThatIsNotExecutable() {
        using var bundle = TestBundle.Create(nameof(SealingRefusesAnEntryPointThatIsNotExecutable));
        var path = bundle.At("Contents/Bin/hello");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var e = await Assert.ThrowsAsync<BundleException>(() => bundle.SealAsync());
        Assert.Equal(BundleFailure.MalformedInfo, e.Failure);
        Assert.Contains("is not executable", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingInfoFromSomewhereThatIsNotABundleSaysSo() {
        using var scratch = TempDirectory.Create(nameof(ReadingInfoFromSomewhereThatIsNotABundleSaysSo));

        var e = await Assert.ThrowsAsync<BundleException>(
            () => BundleSealer.ReadInfoAsync(scratch.Combine("Nothing.app"))
        );

        Assert.Equal(BundleFailure.NotABundle, e.Failure);
        Assert.Contains("it is not an application bundle", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InfoJsonSurvivesTheRoundTripThroughDisk() {
        // The sealer reads Info.json back rather than being handed the object,
        // so the bytes written by the packaging tool have to parse to what it
        // meant. Checked here because everything else in this file depends on it.
        using var bundle = TestBundle.Create(nameof(InfoJsonSurvivesTheRoundTripThroughDisk));

        var info = await BundleSealer.ReadInfoAsync(bundle.Path);

        Assert.Equal("io.trinix.hello", info.Identifier);
        Assert.Equal("Contents/Bin/hello", info.EntryPoint);
        Assert.Equal([BundlePermissions.Display], info.Permissions);
        Assert.Equal(
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(bundle.At(BundleLayout.InfoPath))).TrimEnd('\n'),
            Encoding.UTF8.GetString(BundleJson.ToBytes(info, BundleJson.Default.BundleInfo)).TrimEnd('\n')
        );
    }
}

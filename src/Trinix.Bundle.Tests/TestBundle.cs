using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Trinix.Bundle.Tests;

/// <summary>
///     Builds a real <c>.app</c> on disk, and a real PKI to sign it with.
/// </summary>
/// <remarks>
///     <para>
///         The fixtures here are the reason these tests are worth anything. A
///         hand-written <c>manifest.json</c> next to a hand-written signature would
///         only ever prove that the verifier agrees with whoever wrote the fixture;
///         generating a root, issuing a developer certificate from it and running
///         <see cref="BundleSealer.SealAsync" /> means the tamper cases below are
///         tampering with something that genuinely verified a moment earlier.
///     </para>
///     <para>
///         It is also fast enough to do per test. P-256 key generation is
///         microseconds — <see cref="DeveloperPki" />'s own remarks give that as one of
///         the reasons for choosing it over RSA — so nothing here needs to be shared
///         between tests, and no test can be broken by another test's leftovers.
///     </para>
/// </remarks>
public sealed class TestBundle : IDisposable {
    TestBundle(TempDirectory scratch, string path, X509Certificate2 root, X509Certificate2 identity) {
        Scratch = scratch;
        Path = path;
        Root = root;
        Identity = identity;
    }

    /// <summary>The temporary tree everything lives in.</summary>
    public TempDirectory Scratch { get; }

    /// <summary>The <c>.app</c> directory.</summary>
    public string Path { get; }

    /// <summary>The trust anchor. Give it to <see cref="TrustStore.FromCertificates" />.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>The signing identity, with its private key.</summary>
    public X509Certificate2 Identity { get; }

    /// <summary>A store containing exactly <see cref="Root" />.</summary>
    public TrustStore Trust => TrustStore.FromCertificates([Root], "test");

    /// <inheritdoc />
    public void Dispose() {
        Identity.Dispose();
        Root.Dispose();
        Scratch.Dispose();
    }

    /// <summary>
    ///     An unsealed bundle: <c>Info.json</c>, an executable entry point and one
    ///     resource, plus a freshly generated root and developer certificate.
    /// </summary>
    /// <param name="label">Names the scratch directory, so a leftover is traceable.</param>
    /// <param name="identifier">The application's identity.</param>
    /// <param name="version">Its ordered version.</param>
    /// <param name="notBefore">
    ///     Validity start for the generated certificates. Passed in so that a test can
    ///     ask what the verifier does with a certificate that is not yet valid, or one
    ///     that has expired, without waiting five years.
    /// </param>
    public static TestBundle Create(
        string label,
        string identifier = "io.trinix.hello",
        string version = "1.0.0",
        DateTimeOffset? notBefore = null
    ) {
        var scratch = TempDirectory.Create(label);
        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);

        var root = DeveloperPki.CreateRoot("Trinix Test Root", "Trinix", start);
        var identity = DeveloperPki.CreateIdentity(root, "Trinix Test Developer", "Trinix", start);

        var path = scratch.Combine("Hello.app");
        Directory.CreateDirectory(System.IO.Path.Combine(path, BundleLayout.BinDirectory));
        Directory.CreateDirectory(System.IO.Path.Combine(path, BundleLayout.ResourcesDirectory));

        var info = new BundleInfo {
            Identifier = identifier,
            Name = "Hello",
            Version = version,
            EntryPoint = BundleLayout.BinDirectory + "/hello",
            Permissions = [BundlePermissions.Display]
        };

        File.WriteAllBytes(
            System.IO.Path.Combine(path, BundleLayout.InfoPath),
            BundleJson.ToBytes(info, BundleJson.Default.BundleInfo)
        );

        var bundle = new TestBundle(scratch, path, root, identity);
        bundle.WriteFile(BundleLayout.BinDirectory + "/hello", "#!/bin/sh\necho hello\n", executable: true);
        bundle.WriteFile(BundleLayout.ResourcesDirectory + "/greeting.txt", "hello, world\n");
        return bundle;
    }

    /// <summary>Write a file into the bundle, creating any directories it needs.</summary>
    public void WriteFile(string relativePath, string content, bool executable = false) {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);

        if (executable && !OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(
                full,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            );
        }
    }

    /// <summary>Absolute path of something inside the bundle.</summary>
    public string At(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    ///     Flip one byte in the middle of a file, keeping its length.
    /// </summary>
    /// <remarks>
    ///     ⚠ The length is preserved on purpose. Truncating a file would be caught by
    ///     the manifest's size field before any hash is computed, which proves the
    ///     cheap check works and says nothing about the expensive one. A same-length
    ///     edit is the case the Merkle tree exists for.
    /// </remarks>
    public void FlipAByte(string relativePath) {
        var full = At(relativePath);
        var bytes = File.ReadAllBytes(full);
        Assert.NotEmpty(bytes);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>Seal the bundle with <see cref="Identity" />.</summary>
    public Task<BundleManifest> SealAsync(string architecture = "any", DateTimeOffset? signedAt = null) =>
        BundleSealer.SealAsync(Path, Identity, architecture, signedAt ?? DateTimeOffset.UtcNow);

    /// <summary>Verify against <see cref="Trust" />.</summary>
    public Task<VerificationResult> VerifyAsync(DateTimeOffset? verificationTime = null) =>
        BundleVerifier.VerifyAsync(Path, Trust, verificationTime);

    /// <summary>Read back the manifest the sealer wrote.</summary>
    public async Task<BundleManifest> ReadManifestAsync() {
        var bytes = await File.ReadAllBytesAsync(At(BundleLayout.ManifestPath));
        return JsonSerializer.Deserialize(bytes, BundleJson.Default.BundleManifest)!;
    }

    /// <summary>
    ///     Replace the manifest with <paramref name="manifest" /> and sign it properly.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the fixture for the hardest class of attack the format has to
    ///     survive: a signer the system genuinely trusts, saying something false. The
    ///     chain check and the signature check both pass, so anything caught after
    ///     this is caught by the manifest's own consistency rules rather than by
    ///     cryptography — which is exactly why those rules exist.
    /// </remarks>
    public async Task ReSignAsync(BundleManifest manifest) {
        var bytes = BundleJson.ToBytes(manifest, BundleJson.Default.BundleManifest);
        using var key = Identity.GetECDsaPrivateKey()!;

        await File.WriteAllBytesAsync(At(BundleLayout.ManifestPath), bytes);
        await File.WriteAllBytesAsync(
            At(BundleLayout.SignaturePath),
            key.SignData(bytes, HashAlgorithmName.SHA256, BundleSealer.SignatureFormat)
        );
    }

    /// <summary>
    ///     A copy of <paramref name="manifest" /> with one field replaced.
    /// </summary>
    /// <remarks>
    ///     <see cref="BundleManifest" /> is a class with <c>init</c> properties rather
    ///     than a record, so there is no <c>with</c> expression to reach for.
    /// </remarks>
    public static BundleManifest Rewrite(
        BundleManifest manifest,
        string? identifier = null,
        string? merkleRoot = null,
        IReadOnlyList<ManifestEntry>? entries = null
    ) {
        ArgumentNullException.ThrowIfNull(manifest);

        return new() {
            Schema = manifest.Schema,
            Identifier = identifier ?? manifest.Identifier,
            Version = manifest.Version,
            Architecture = manifest.Architecture,
            SignedAt = manifest.SignedAt,
            MerkleRoot = merkleRoot ?? manifest.MerkleRoot,
            Entries = entries ?? manifest.Entries
        };
    }
}

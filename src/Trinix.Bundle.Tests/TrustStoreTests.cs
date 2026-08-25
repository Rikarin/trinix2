using System.Security.Cryptography.X509Certificates;

namespace Trinix.Bundle.Tests;

/// <summary>
///     The set of roots Trinix will accept a signature from.
/// </summary>
/// <remarks>
///     The interesting behaviour here is the refusal to produce an empty store. An
///     empty trust store rejects every bundle, which looks exactly like a signature
///     problem and sends whoever is debugging it to entirely the wrong place — so
///     <see cref="TrustStore.Load" /> throws, with the rejected files named. The tests
///     below are mostly about that error path, because the happy path is one line and
///     the error path is where the hours go.
/// </remarks>
public class TrustStoreTests {
    static string WriteRoot(TempDirectory directory, string name, string commonName = "Trinix Test Root") {
        using var root = DeveloperPki.CreateRoot(commonName, "Trinix", DateTimeOffset.UtcNow.AddDays(-1));
        var path = directory.Combine(name);
        File.WriteAllText(path, root.ExportCertificatePem() + "\n");
        return path;
    }

    [Fact]
    public void LoadsEveryPemInTheDirectory() {
        using var scratch = TempDirectory.Create(nameof(LoadsEveryPemInTheDirectory));
        WriteRoot(scratch, "one.pem", "Root One");
        WriteRoot(scratch, "two.pem", "Root Two");

        var store = TrustStore.Load(scratch.Path);

        Assert.Equal(2, store.Count);
        Assert.Equal(scratch.Path, store.Source);
        Assert.Contains(store.Describe(), d => d.Contains("Root One", StringComparison.Ordinal));
        Assert.Contains(store.Describe(), d => d.Contains("Root Two", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoresFilesThatAreNotPem() {
        using var scratch = TempDirectory.Create(nameof(IgnoresFilesThatAreNotPem));
        WriteRoot(scratch, "root.pem");
        File.WriteAllText(scratch.Combine("README.md"), "roots go here");
        File.WriteAllText(scratch.Combine("root.pem.bak"), "not a certificate");

        Assert.Equal(1, TrustStore.Load(scratch.Path).Count);
    }

    [Fact]
    public void AMissingDirectoryIsAnErrorWithTheReasonInIt() {
        var e = Assert.Throws<BundleException>(
            () => TrustStore.Load(Path.Combine(Path.GetTempPath(), "trinix-no-such-trust-store"))
        );

        Assert.Equal(BundleFailure.NoTrustStore, e.Failure);
        Assert.Contains("the system image carries the roots", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyDirectoryIsAnErrorRatherThanAnEmptyStore() {
        using var scratch = TempDirectory.Create(nameof(AnEmptyDirectoryIsAnErrorRatherThanAnEmptyStore));

        var e = Assert.Throws<BundleException>(() => TrustStore.Load(scratch.Path));

        Assert.Equal(BundleFailure.NoTrustStore, e.Failure);
        Assert.Contains("contains no usable root", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadablePemIsNamedInTheError() {
        using var scratch = TempDirectory.Create(nameof(AnUnreadablePemIsNamedInTheError));
        File.WriteAllText(scratch.Combine("broken.pem"), "-----BEGIN CERTIFICATE-----\nnot base64\n");

        var e = Assert.Throws<BundleException>(() => TrustStore.Load(scratch.Path));

        Assert.Equal(BundleFailure.NoTrustStore, e.Failure);
        Assert.Contains("broken.pem", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeveloperCertificateIsNotATrustAnchor() {
        // ⚠ The mistake this catches is someone copying their signing
        // certificate into the roots directory. It is not a CA, so it cannot
        // sign anything, and a store that accepted it would trust nothing while
        // appearing to be configured.
        using var scratch = TempDirectory.Create(nameof(ADeveloperCertificateIsNotATrustAnchor));
        using var root = DeveloperPki.CreateRoot("Trinix Test Root", "Trinix", DateTimeOffset.UtcNow.AddDays(-1));
        using var identity = DeveloperPki.CreateIdentity(root, "A Developer", "Trinix", DateTimeOffset.UtcNow.AddDays(-1));
        File.WriteAllText(scratch.Combine("developer.pem"), identity.ExportCertificatePem() + "\n");

        var e = Assert.Throws<BundleException>(() => TrustStore.Load(scratch.Path));

        Assert.Contains("not a certificate authority", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootBesideADeveloperCertificateStillLoads() {
        using var scratch = TempDirectory.Create(nameof(ARootBesideADeveloperCertificateStillLoads));
        WriteRoot(scratch, "root.pem");
        using var other = DeveloperPki.CreateRoot("Issuer", "Trinix", DateTimeOffset.UtcNow.AddDays(-1));
        using var identity = DeveloperPki.CreateIdentity(other, "A Developer", "Trinix", DateTimeOffset.UtcNow.AddDays(-1));
        File.WriteAllText(scratch.Combine("developer.pem"), identity.ExportCertificatePem() + "\n");

        Assert.Equal(1, TrustStore.Load(scratch.Path).Count);
    }

    [Fact]
    public void FromCertificatesTakesWhatItIsGiven() {
        using var root = DeveloperPki.CreateRoot("Trinix Test Root", "Trinix", DateTimeOffset.UtcNow.AddDays(-1));

        var store = TrustStore.FromCertificates([root], "in-memory");

        Assert.Equal(1, store.Count);
        Assert.Equal("in-memory", store.Source);
    }

    [Fact]
    public void FromCertificatesRejectsNull() {
        Assert.Throws<ArgumentNullException>(() => TrustStore.FromCertificates(null!, "nowhere"));
    }

    [Fact]
    public void TheSystemDirectoryIsInsideTheReadOnlyImage() {
        // /usr/share rather than /etc: adding a root means replacing the image,
        // which is an A/B update that is itself signed. There is deliberately no
        // way to add one at runtime.
        Assert.Equal("/usr/share/trinix/pki/roots", TrustStore.SystemDirectory);
    }

    [Fact]
    public void DescribeNamesEachRootAndAShortThumbprint() {
        using var scratch = TempDirectory.Create(nameof(DescribeNamesEachRootAndAShortThumbprint));
        WriteRoot(scratch, "root.pem", "Trinix Root CA");

        var description = Assert.Single(TrustStore.Load(scratch.Path).Describe());

        Assert.Contains("Trinix Root CA", description, StringComparison.Ordinal);
        // 16 hex characters of SHA-256, in parentheses at the end.
        Assert.Matches(@"\([0-9a-f]{16}\)$", description);
    }

    [Fact]
    public async Task LoadedRootsAreUsableAsChainAnchors() {
        // The store is only worth anything if what comes out of it is what the
        // verifier can build a chain to — so this reads a root back off disk
        // and verifies a bundle against it, rather than trusting the count.
        using var bundle = TestBundle.Create(nameof(LoadedRootsAreUsableAsChainAnchors));
        using var roots = TempDirectory.Create(nameof(LoadedRootsAreUsableAsChainAnchors) + "-roots");
        File.WriteAllText(roots.Combine("root.pem"), bundle.Root.ExportCertificatePem() + "\n");

        await bundle.SealAsync();

        var result = await BundleVerifier.VerifyAsync(bundle.Path, TrustStore.Load(roots.Path));
        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void RootsRoundTripThroughPemWithoutLosingTheirConstraints() {
        using var scratch = TempDirectory.Create(nameof(RootsRoundTripThroughPemWithoutLosingTheirConstraints));
        var path = WriteRoot(scratch, "root.pem");

        var reloaded = X509Certificate2.CreateFromPem(File.ReadAllText(path));
        var constraints = reloaded.Extensions.OfType<X509BasicConstraintsExtension>().Single();

        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(1, constraints.PathLengthConstraint);
    }
}

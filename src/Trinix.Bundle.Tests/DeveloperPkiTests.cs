using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Trinix.Bundle.Tests;

/// <summary>
///     The certificate profiles, which are the policy.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="DeveloperPki" /> exists so that <c>pathLenConstraint</c>, key
///         usage and the code-signing EKU are three lines that can be read together
///         instead of a directory of <c>openssl.cnf</c> fragments nobody dares change.
///         The tests assert those three constraints directly, because they are the
///         difference between a delegation and a franchise, and because a certificate
///         that is wrong in this way still works — right up until it is used to sign
///         something it should not have been able to.
///     </para>
///     <para>
///         ⚠ These do not test that ECDSA works. They test that Trinix asked for the
///         right certificate.
///     </para>
/// </remarks>
public class DeveloperPkiTests {
    static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARootIsACertificateAuthorityThatMayDelegateExactlyOnce() {
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        var constraints = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(1, constraints.PathLengthConstraint);
        Assert.True(constraints.Critical);
    }

    [Fact]
    public void ARootMaySignCertificatesAndNothingElse() {
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        var usage = root.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);
        Assert.False(usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
    }

    [Fact]
    public void ARootIsSelfSignedAndValidForTwentyYears() {
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        Assert.Equal(root.Subject, root.Issuer);
        Assert.Equal(NotBefore.UtcDateTime, root.NotBefore.ToUniversalTime());
        Assert.Equal(NotBefore.AddYears(DeveloperPki.RootYears).UtcDateTime, root.NotAfter.ToUniversalTime());
    }

    [Fact]
    public void AnIdentityIsNotACertificateAuthority() {
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);
        using var identity = DeveloperPki.CreateIdentity(root, "A Developer", "Trinix", NotBefore);

        var constraints = identity.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);
    }

    [Fact]
    public void AnIdentityCarriesTheCodeSigningEkuCritically() {
        // Critical, so that a verifier which does not understand code signing
        // refuses the certificate rather than accepting it for something else.
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);
        using var identity = DeveloperPki.CreateIdentity(root, "A Developer", "Trinix", NotBefore);

        var eku = identity.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.True(eku.Critical);
        Assert.Equal([BundleLayout.CodeSigningEku], eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value!).ToArray());
    }

    [Fact]
    public void AnIdentityIsIssuedByTheRootAndLastsFiveYears() {
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);
        using var identity = DeveloperPki.CreateIdentity(root, "A Developer", "Trinix", NotBefore);

        Assert.Equal(root.Subject, identity.Issuer);
        Assert.NotEqual(identity.Subject, identity.Issuer);
        Assert.Equal(NotBefore.AddYears(DeveloperPki.IdentityYears).UtcDateTime, identity.NotAfter.ToUniversalTime());
        Assert.True(identity.HasPrivateKey);
    }

    [Fact]
    public void SerialNumbersAreUnpredictableAndPositive() {
        // ⚠ A DER INTEGER with the high bit set is negative, and a negative
        // serial is a certificate some verifiers reject and others renumber.
        // Random is what makes a hash-collision forgery impractical; positive
        // is what makes the certificate parse everywhere.
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        var serials = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 8; i++) {
            using var identity = DeveloperPki.CreateIdentity(root, "A Developer", "Trinix", NotBefore);
            Assert.True(serials.Add(identity.SerialNumber));

            var first = Convert.FromHexString(identity.SerialNumber)[0];
            Assert.True(first is > 0 and <= 0x7F, $"serial starts with 0x{first:x2}");
        }
    }

    [Fact]
    public void AnOrdinaryNameProducesExactlyTwoAttributes() {
        // CN and O, one attribute each — which is the shape everything that
        // compares subjects (the chain builder, the install receipt, the line
        // the launcher shows a person) assumes.
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        Assert.Equal(
            ["Trinix Root", "Trinix"],
            root.SubjectName.EnumerateRelativeDistinguishedNames().Select(r => r.GetSingleElementValue()!).ToArray()
        );
    }

    // -------------------------------------------------------------------------
    // ⚠ A finding, not a specification. The two tests below record what
    // DeveloperPki.Escape actually does, which is not what it says it does.
    //
    // Escape() prefixes RFC 4514's special characters with a backslash, so that
    // an organisation called "Trinix, Inc." becomes one attribute rather than
    // two. .NET's X500DistinguishedName *string* parser does not implement RFC
    // 4514 escaping, and the result splits two ways:
    //
    //   * "," and ";" — its own delimiters — make it throw outright, so an
    //     identity for an organisation with a comma in its name cannot be
    //     created at all;
    //   * every other special character keeps the backslash inside the
    //     attribute value, so the common name really is `Trinix\+Root`.
    //
    // Both are pinned rather than fixed. They fail visibly and early — an
    // exception at key generation, or a subject that is obviously wrong the
    // first time anyone reads it — never a certificate that quietly claims to
    // be something else, so nothing downstream is unsafe today. Doing it
    // properly means choosing between quoting and escaping, handling leading
    // and trailing spaces and the '#' case, and probably building the
    // X500DistinguishedName from an X500DistinguishedNameBuilder instead of a
    // string. That belongs to whoever owns the PKI; these two tests are what
    // they should delete when they do it.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    public void ASeparatorInANameIsRefusedOutright(string separator) {
        Assert.Throws<CryptographicException>(
            () => DeveloperPki.CreateRoot("Trinix Root", "Trinix" + separator + " Inc.", NotBefore)
        );
    }

    [Theory]
    [InlineData("+")]
    [InlineData("\"")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("=")]
    public void AnyOtherSpecialCharacterKeepsItsBackslashInsideTheValue(string special) {
        using var root = DeveloperPki.CreateRoot("Trinix" + special + "Root", "Trinix", NotBefore);

        // The good half: still one attribute, so nothing is being smuggled in
        // as an extra RDN.
        Assert.Equal(2, root.SubjectName.EnumerateRelativeDistinguishedNames().Count());

        // The bad half: the escape is data, not syntax.
        Assert.Equal(
            ["Trinix\\" + special + "Root", "Trinix"],
            root.SubjectName.EnumerateRelativeDistinguishedNames().Select(r => r.GetSingleElementValue()!).ToArray()
        );
    }

    [Fact]
    public void SaveAndLoadRoundTripACertificateWithItsKey() {
        using var scratch = TempDirectory.Create(nameof(SaveAndLoadRoundTripACertificateWithItsKey));
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);
        using var identity = DeveloperPki.CreateIdentity(root, "A Developer", "Trinix", NotBefore);

        var certificatePath = scratch.Combine("developer.pub.pem");
        var keyPath = scratch.Combine("developer.key.pem");
        DeveloperPki.Save(identity, certificatePath, keyPath);

        using var loaded = DeveloperPki.Load(certificatePath, keyPath);

        Assert.Equal(identity.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void TheSavedKeyIsReadableOnlyByItsOwner() {
        // ⚠ The key file's permissions are its only protection.
        using var scratch = TempDirectory.Create(nameof(TheSavedKeyIsReadableOnlyByItsOwner));
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        var keyPath = scratch.Combine("root.key.pem");
        DeveloperPki.Save(root, scratch.Combine("root.pub.pem"), keyPath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
    }

    [Fact]
    public void TheCertificateAndTheKeyAreSeparateFiles() {
        // Two files rather than a PKCS#12, because the split is what
        // .gitignore can act on: "*.key.pem" is a rule, "do not commit this
        // one" is a hope.
        using var scratch = TempDirectory.Create(nameof(TheCertificateAndTheKeyAreSeparateFiles));
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);

        var certificatePath = scratch.Combine("root.pub.pem");
        var keyPath = scratch.Combine("root.key.pem");
        DeveloperPki.Save(root, certificatePath, keyPath);

        var certificate = File.ReadAllText(certificatePath);
        Assert.Contains("-----BEGIN CERTIFICATE-----", certificate, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", certificate, StringComparison.Ordinal);
        Assert.Contains("-----BEGIN PRIVATE KEY-----", File.ReadAllText(keyPath), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingAnIdentityThatIsNotThereSaysWhereItLooked() {
        using var scratch = TempDirectory.Create(nameof(LoadingAnIdentityThatIsNotThereSaysWhereItLooked));

        var e = Assert.Throws<BundleException>(
            () => DeveloperPki.Load(scratch.Combine("absent.pub.pem"), scratch.Combine("absent.key.pem"))
        );

        Assert.Equal(BundleFailure.MalformedSignature, e.Failure);
        Assert.Contains("absent.pub.pem", e.Message, StringComparison.Ordinal);
        Assert.Contains("absent.key.pem", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingACertificateWithNoPrivateKeyIsRefused() {
        using var scratch = TempDirectory.Create(nameof(SavingACertificateWithNoPrivateKeyIsRefused));
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);
        using var publicOnly = X509Certificate2.CreateFromPem(root.ExportCertificatePem());

        var e = Assert.Throws<BundleException>(
            () => DeveloperPki.Save(publicOnly, scratch.Combine("x.pub.pem"), scratch.Combine("x.key.pem"))
        );

        Assert.Equal(BundleFailure.MalformedSignature, e.Failure);
    }

    [Fact]
    public void TheKeysAreP256() {
        // Not interoperability theatre: the verifier runs on every application
        // launch, and P-256 keeps a signature at 71 bytes rather than 256.
        using var root = DeveloperPki.CreateRoot("Trinix Root", "Trinix", NotBefore);
        using var key = root.GetECDsaPublicKey();

        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void CreateIdentityRejectsANullIssuer() {
        Assert.Throws<ArgumentNullException>(
            () => DeveloperPki.CreateIdentity(null!, "A Developer", "Trinix", NotBefore)
        );
    }
}

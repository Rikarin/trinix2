using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Trinix.Bundle;

/// <summary>
/// Trinix's certificate authority, as code rather than as a page of
/// <c>openssl</c> invocations.
/// </summary>
/// <remarks>
/// <para>
/// The profiles here are the policy: a root that may sign certificates and
/// nothing else, and a developer certificate that may sign code and nothing
/// else. Writing them out as <c>openssl.cnf</c> sections and shell scripts
/// works, and every project that does it accumulates a directory of files
/// nobody dares change because the relationship between them is implicit. Here
/// the relationship is a function call, and the constraints that matter —
/// <c>pathLenConstraint</c>, key usage, the code-signing EKU — are three lines
/// that can be read together.
/// </para>
/// <para>
/// P-256 rather than RSA: the signatures are 71 bytes instead of 256, key
/// generation is instant rather than seconds, and the verifier runs on every
/// application launch. Nothing in the trust path needs RSA's interoperability.
/// </para>
/// </remarks>
public static class DeveloperPki
{
    /// <summary>How long a generated root is valid for.</summary>
    public const int RootYears = 20;

    /// <summary>How long a generated developer certificate is valid for.</summary>
    public const int IdentityYears = 5;

    /// <summary>
    /// Create a self-signed root certificate authority.
    /// </summary>
    /// <param name="commonName">The name it will be known by.</param>
    /// <param name="organisation">The organisation it speaks for.</param>
    /// <param name="notBefore">
    /// Validity start. Passed in rather than taken from the clock so that a
    /// caller regenerating a development PKI can produce the same validity
    /// window twice; the key is still fresh every time.
    /// </param>
    public static X509Certificate2 CreateRoot(string commonName, string organisation, DateTimeOffset notBefore)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            Distinguished(commonName, organisation), key, HashAlgorithmName.SHA256);

        // pathLenConstraint = 1: this root may issue an intermediate, and that
        // intermediate may issue leaves. It may not produce a chain deeper than
        // that, which is the difference between a delegation and a franchise.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 1, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(notBefore, notBefore.AddYears(RootYears));
    }

    /// <summary>
    /// Issue a developer certificate from <paramref name="issuer"/>.
    /// </summary>
    /// <returns>The certificate with its private key attached, ready to sign with.</returns>
    public static X509Certificate2 CreateIdentity(
        X509Certificate2 issuer,
        string commonName,
        string organisation,
        DateTimeOffset notBefore)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            Distinguished(commonName, organisation), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: true));
        // Critical, so that a verifier which does not understand code signing
        // refuses the certificate rather than accepting it for something else.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(BundleLayout.CodeSigningEku)], critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        // Serial numbers must be unique per issuer and unpredictable enough not
        // to be forgeable in a hash collision attack against the signature.
        // Random is both, and there is no registry to keep in step with.
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;      // positive: a DER INTEGER with the high bit set is negative
        if (serial[0] == 0) { serial[0] = 1; }

        using X509Certificate2 issued = request.Create(issuer, notBefore, notBefore.AddYears(IdentityYears), serial);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Write a certificate and its private key as two PEM files.
    /// </summary>
    /// <remarks>
    /// Two files rather than a PKCS#12, because the split is what
    /// <c>.gitignore</c> can act on: the repository refuses <c>*.key.pem</c>
    /// everywhere under <c>signing/</c> and permits <c>*.pub.pem</c>. A single
    /// container holding both would make that rule impossible to express, and
    /// "do not commit this one" is not a rule, it is a hope.
    /// </remarks>
    public static void Save(X509Certificate2 certificate, string certificatePath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(certificatePath))!);
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem() + "\n");

        using ECDsa key = certificate.GetECDsaPrivateKey()
            ?? throw new BundleException(BundleFailure.MalformedSignature, "certificate has no ECDSA private key to save");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem() + "\n");

        // The key file's permissions are its only protection. Set before it has
        // anything in it would be better still, but .NET has no atomic
        // create-with-mode; the window is one process on a machine that is
        // already trusted with the key.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Load a certificate and private key written by <see cref="Save"/>.</summary>
    public static X509Certificate2 Load(string certificatePath, string keyPath)
    {
        if (!File.Exists(certificatePath) || !File.Exists(keyPath))
        {
            throw new BundleException(
                BundleFailure.MalformedSignature,
                $"no signing identity at {certificatePath} (+ {Path.GetFileName(keyPath)})");
        }
        return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
    }

    private static string Distinguished(string commonName, string organisation) =>
        string.Create(CultureInfo.InvariantCulture, $"CN={Escape(commonName)}, O={Escape(organisation)}");

    // RFC 4514's special characters. A comma in an organisation name would
    // otherwise silently become a second relative distinguished name.
    private static string Escape(string value)
    {
        Span<char> special = [',', '+', '"', '\\', '<', '>', ';', '='];
        var result = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (special.Contains(c)) { result.Append('\\'); }
            result.Append(c);
        }
        return result.ToString();
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Trinix.Bundle;

/// <summary>
///     The roots Trinix will accept a signature from.
/// </summary>
/// <remarks>
///     <para>
///         Read from a directory in the read-only system image and from nowhere else.
///         Not the operating system's certificate store: that store exists to decide
///         which websites are real, it is large, it is maintained by other people, and
///         any of several hundred CAs in it being persuaded to issue a code-signing
///         certificate would otherwise be enough to install software on a Trinix
///         machine. The two questions have nothing to do with each other and share no
///         trust.
///     </para>
///     <para>
///         The store being part of the signed system image is what gives it its
///         integrity: adding a root means replacing the image, which is an A/B update
///         that is itself signed. There is deliberately no way to add a root at
///         runtime, and there will not be one until there is a policy for who may.
///     </para>
/// </remarks>
public sealed class TrustStore {
    /// <summary>Where the image keeps them.</summary>
    public const string SystemDirectory = "/usr/share/trinix/pki/roots";

    /// <summary>Where these roots were read from, for diagnostics.</summary>
    public string Source { get; }

    /// <summary>The anchors themselves.</summary>
    public X509Certificate2Collection Roots { get; }

    /// <summary>How many there are.</summary>
    public int Count => Roots.Count;

    TrustStore(X509Certificate2Collection roots, string source) {
        Roots = roots;
        Source = source;
    }

    /// <summary>
    ///     Load every <c>*.pem</c> in a directory as a trust anchor.
    /// </summary>
    /// <exception cref="BundleException">
    ///     The directory is missing or holds nothing usable. Deliberately an error
    ///     rather than an empty store: an empty store rejects everything, which
    ///     looks exactly like a signature problem and sends whoever is debugging it
    ///     to the wrong place entirely.
    /// </exception>
    public static TrustStore Load(string? directory = null) {
        var path = directory ?? SystemDirectory;

        if (!Directory.Exists(path)) {
            throw new BundleException(
                BundleFailure.NoTrustStore,
                $"no trust store at {path} — the system image carries the roots Trinix accepts"
            );
        }

        var roots = new X509Certificate2Collection();
        List<string> rejected = [];

        foreach (var file in Directory.EnumerateFiles(path, "*.pem").Order(StringComparer.Ordinal)) {
            X509Certificate2 certificate;
            try {
                // CreateFromPem, not CreateFromPemFile: the single-argument
                // file form looks for a private key in the same file and fails
                // when it does not find one. A trust anchor is a certificate
                // and nothing else, which is the entire point of it.
                certificate = X509Certificate2.CreateFromPem(File.ReadAllText(file));
            } catch (CryptographicException e) {
                rejected.Add($"{Path.GetFileName(file)}: {e.Message}");
                continue;
            }

            // A trust anchor that is not a CA cannot sign anything, so its
            // presence here is a mistake worth naming rather than ignoring —
            // most likely someone copied a developer certificate in.
            var constraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault();
            if (constraints is null || !constraints.CertificateAuthority) {
                rejected.Add($"{Path.GetFileName(file)}: not a certificate authority");
                certificate.Dispose();
                continue;
            }

            roots.Add(certificate);
        }

        if (roots.Count == 0) {
            var detail = rejected.Count > 0
                ? " (" + string.Join("; ", rejected) + ")"
                : string.Empty;
            throw new BundleException(
                BundleFailure.NoTrustStore,
                $"the trust store at {path} contains no usable root{detail}"
            );
        }

        return new(roots, path);
    }

    /// <summary>A store built from certificates already in hand. Used by the signing tools.</summary>
    public static TrustStore FromCertificates(IEnumerable<X509Certificate2> certificates, string source) {
        ArgumentNullException.ThrowIfNull(certificates);

        var roots = new X509Certificate2Collection();
        foreach (var certificate in certificates) {
            roots.Add(certificate);
        }

        return new(roots, source);
    }

    /// <summary>The subjects, for a human-readable report.</summary>
    public IEnumerable<string> Describe() {
        foreach (var root in Roots) {
            yield return $"{root.Subject} ({Hex.Of(root.GetCertHash(HashAlgorithmName.SHA256))[..16]})";
        }
    }
}

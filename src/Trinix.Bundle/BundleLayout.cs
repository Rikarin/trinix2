namespace Trinix.Bundle;

/// <summary>
///     Where things live inside a <c>.app</c> bundle, and the rules about what may
///     be there at all.
/// </summary>
/// <remarks>
///     <para>
///         The shape is macOS's, with one deliberate rename: the executable directory
///         is <c>Contents/Bin</c> rather than <c>Contents/MacOS</c>. The Apple name is
///         a historical accident that would read as a mistake on a Linux system, and
///         nothing depends on it — Trinix's launcher reads the entry point out of
///         <c>Info.json</c> rather than guessing at a path.
///     </para>
///     <para>
///         The metadata file is JSON rather than the TOML the plan offered as an
///         alternative. Three reasons, in order of weight: <c>System.Text.Json</c> has
///         a source generator, so parsing an untrusted manifest needs no reflection and
///         survives trimming and AOT; PowerShell reads JSON natively, which matters
///         when the shell is the system's administrative surface; and adding a TOML
///         parser would break the no-dependencies rule this assembly is built on.
///     </para>
/// </remarks>
public static class BundleLayout {
    /// <summary>The suffix that makes a directory an application bundle.</summary>
    public const string Extension = ".app";

    /// <summary>The distribution image's file extension — Trinix's <c>.dmg</c>.</summary>
    public const string ImageExtension = ".tdi";

    /// <summary>Everything in a bundle lives under this directory.</summary>
    public const string Contents = "Contents";

    /// <summary>Identity, version, entry point and declared permissions.</summary>
    public const string InfoPath = "Contents/Info.json";

    /// <summary>Executables. The entry point named by <c>Info.json</c> is normally here.</summary>
    public const string BinDirectory = "Contents/Bin";

    /// <summary>Data files: images, keymaps, localisations.</summary>
    public const string ResourcesDirectory = "Contents/Resources";

    /// <summary>Private shared libraries and managed assemblies.</summary>
    public const string FrameworksDirectory = "Contents/Frameworks";

    /// <summary>
    ///     The signature. Everything under here is excluded from the manifest, for
    ///     the obvious reason that it cannot cover itself.
    /// </summary>
    public const string SignatureDirectory = "Contents/_Signature";

    /// <summary>The signed document: the file list, their hashes, and the Merkle root.</summary>
    public const string ManifestPath = "Contents/_Signature/manifest.json";

    /// <summary>A raw ECDSA signature, DER-encoded, over the bytes of <see cref="ManifestPath" />.</summary>
    public const string SignaturePath = "Contents/_Signature/manifest.sig";

    /// <summary>
    ///     The signer's certificate, leaf first, then any intermediates, as PEM.
    ///     The root is deliberately absent: a chain that carried its own root would
    ///     invite verifying against it.
    /// </summary>
    public const string CertificatesPath = "Contents/_Signature/certificates.pem";

    /// <summary>
    ///     Object identifier for the Code Signing extended key usage (RFC 5280).
    ///     A certificate without it cannot sign a Trinix bundle, which is what
    ///     stops a TLS certificate from doubling as a code-signing one.
    /// </summary>
    public const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    /// <summary>
    ///     Is <paramref name="bundleRelativePath" /> excluded from the signed manifest?
    /// </summary>
    public static bool IsSignatureMaterial(string bundleRelativePath) =>
        bundleRelativePath.Equals(SignatureDirectory, StringComparison.Ordinal)
        || bundleRelativePath.StartsWith(SignatureDirectory + "/", StringComparison.Ordinal);

    /// <summary>
    ///     The bundle's display name: the directory name without the extension.
    /// </summary>
    public static string NameOf(string bundlePath) {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(bundlePath));
        return name.EndsWith(Extension, StringComparison.Ordinal)
            ? name[..^Extension.Length]
            : name;
    }

    /// <summary>
    ///     Reject a path that would let a bundle write outside itself when it is
    ///     unpacked, or name something that is not in the bundle at all.
    /// </summary>
    /// <remarks>
    ///     This is checked on the way in (when sealing) and again on the way out
    ///     (when verifying a manifest someone else produced). The second is the one
    ///     that matters: a manifest is attacker-controlled input until its
    ///     signature has been checked, and even afterwards it only proves that a
    ///     developer signed it, not that the developer was careful.
    /// </remarks>
    public static bool IsSafeRelativePath(string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal)) {
            return false;
        }

        if (path.Contains("//", StringComparison.Ordinal)) {
            return false;
        }

        foreach (var segment in path.Split('/')) {
            if (segment.Length == 0) {
                return false;
            }

            if (segment is "." or "..") {
                return false;
            }
        }

        return true;
    }
}

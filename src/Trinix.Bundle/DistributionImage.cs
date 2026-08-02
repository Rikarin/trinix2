using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Trinix.Bundle;

/// <summary>
/// The <c>.tdi</c> distribution image — Trinix's answer to a <c>.dmg</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.tdi</c> is an EROFS filesystem image containing exactly one
/// <c>.app</c> bundle, with a small signed footer appended after it. Mounting
/// it is the ordinary <c>mount -t erofs</c>; the kernel addresses blocks from
/// the superblock and never reads past the last one, so the footer is invisible
/// to it. That is what lets one file be both a mountable filesystem and a
/// signed artifact, with no wrapper to strip and no second file to lose.
/// </para>
/// <para>
/// EROFS rather than SquashFS because it is the modern read-only filesystem in
/// the kernel, its metadata is laid out for random access rather than
/// decompressed in stream order, and its userspace tool takes a directory
/// directly — which matters here because the whole build runs unprivileged in a
/// container and cannot use a loop device to construct an image.
/// </para>
/// <para>
/// The image signature is not a duplicate of the bundle signature inside it.
/// The bundle signature says "these files are what the developer built"; the
/// image signature says "this artifact, whole, is what the developer
/// published". Only the second can be checked before mounting, and mounting an
/// untrusted filesystem image hands attacker-controlled data to a kernel
/// filesystem driver — which is precisely the thing to avoid doing first.
/// </para>
/// </remarks>
public static class DistributionImage
{
    /// <summary>The last eight bytes of every <c>.tdi</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "TRINIXDI"u8;

    /// <summary>The footer format this implementation writes.</summary>
    public const uint FormatVersion = 1;

    /// <summary>Bytes of footer, fixed for <see cref="FormatVersion"/>.</summary>
    public const int FooterLength = 96;

    /// <summary>
    /// The block size the image is built with.
    /// </summary>
    /// <remarks>
    /// EROFS refuses to mount an image whose block size exceeds the kernel's
    /// page size, so this is pinned at 4 KiB rather than left to default to the
    /// build machine's page size — a container on an Apple Silicon Mac has
    /// 16 KiB pages, and an image built there would be unmountable on every
    /// Trinix machine.
    /// </remarks>
    public const int BlockSize = 4096;

    /// <summary>What a footer says.</summary>
    /// <param name="PayloadLength">Bytes of EROFS image, from offset zero.</param>
    /// <param name="PayloadSha256">SHA-256 over exactly those bytes.</param>
    /// <param name="SignatureOffset">Where the detached signature starts.</param>
    /// <param name="SignatureLength">How long it is.</param>
    /// <param name="CertificatesOffset">Where the PEM chain starts.</param>
    /// <param name="CertificatesLength">How long it is.</param>
    public readonly record struct Footer(
        long PayloadLength,
        byte[] PayloadSha256,
        long SignatureOffset,
        int SignatureLength,
        long CertificatesOffset,
        int CertificatesLength);

    /// <summary>
    /// The bytes an image signature covers.
    /// </summary>
    /// <remarks>
    /// Length and hash together, behind a domain-separating prefix. The length
    /// is in there because a hash alone says nothing about where the filesystem
    /// ends, and an image whose payload could be re-declared as longer would let
    /// signed bytes be reinterpreted as a different filesystem.
    /// </remarks>
    public static byte[] SignedDescriptor(long payloadLength, ReadOnlySpan<byte> payloadSha256)
    {
        byte[] prefix = Encoding.UTF8.GetBytes("trinix-tdi/1\n");
        var buffer = new byte[prefix.Length + 8 + payloadSha256.Length];
        prefix.CopyTo(buffer, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(prefix.Length), payloadLength);
        payloadSha256.CopyTo(buffer.AsSpan(prefix.Length + 8));
        return buffer;
    }

    /// <summary>
    /// Build and sign a <c>.tdi</c> around one bundle.
    /// </summary>
    /// <param name="bundlePath">An already-sealed <c>.app</c> directory.</param>
    /// <param name="outputPath">The <c>.tdi</c> to write.</param>
    /// <param name="identity">The signing certificate and its private key.</param>
    /// <param name="buildTime">
    /// Stamped into every inode, along with a fixed owner and a filesystem UUID
    /// derived from the bundle's name — so that nothing about *when or where*
    /// the image was built leaks into it.
    /// <para>
    /// That does not make a <c>.tdi</c> byte-reproducible, and it is worth being
    /// precise about why: the bundle inside carries its own signature, ECDSA is
    /// randomised, and the signing time is recorded in the manifest. Two builds
    /// of identical source produce identical <i>content</i> — the same Merkle
    /// root — inside two images that differ. The Merkle root is therefore the
    /// thing to compare when asking whether two builds agree; the file digest
    /// is not.
    /// </para>
    /// </param>
    /// <param name="compress">Compress the payload with LZ4.</param>
    /// <param name="intermediates">Certificates between the signer and the root.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<Footer> PackAsync(
        string bundlePath,
        string outputPath,
        X509Certificate2 identity,
        DateTimeOffset buildTime,
        bool compress = false,
        IReadOnlyList<X509Certificate2>? intermediates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(identity);

        // mkfs.erofs takes a directory and writes every entry under it at the
        // image root, so the bundle is staged inside a scratch directory whose
        // only child is the bundle. Passing the bundle itself would produce an
        // image whose root *is* Contents/, and the .app name — which is the
        // application's name on screen — would be lost.
        string staging = Path.Combine(
            Path.GetTempPath(), "trinix-tdi-" + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(staging);
        try
        {
            string bundleName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundlePath)));
            CopyTree(bundlePath, Path.Combine(staging, bundleName));

            if (File.Exists(outputPath)) { File.Delete(outputPath); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            List<string> arguments =
            [
                "-b", BlockSize.ToString(CultureInfo.InvariantCulture),
                // Reproducibility, three ways: one timestamp for every inode,
                // one owner, one filesystem UUID derived from the identity
                // rather than from /dev/urandom.
                "-T", buildTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                "--all-time",
                "--all-root",
                "-U", DeriveUuid(bundleName).ToString(),
                "-L", Truncate(BundleLayout.NameOf(bundleName), 15),
            ];
            if (compress) { arguments.AddRange(["-zlz4"]); }
            arguments.Add(outputPath);
            arguments.Add(staging);

            await RunAsync("mkfs.erofs", arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }

        long payloadLength = new FileInfo(outputPath).Length;
        byte[] payloadHash = await Hex.HashFileAsync(outputPath, cancellationToken).ConfigureAwait(false);

        using ECDsa? key = identity.GetECDsaPrivateKey();
        if (key is null)
        {
            throw new BundleException(BundleFailure.MalformedSignature, "the signing identity has no ECDSA private key");
        }
        byte[] signature = key.SignData(
            SignedDescriptor(payloadLength, payloadHash), HashAlgorithmName.SHA256, BundleSealer.SignatureFormat);

        var pem = new StringBuilder();
        pem.AppendLine(identity.ExportCertificatePem());
        foreach (X509Certificate2 intermediate in intermediates ?? [])
        {
            pem.AppendLine(intermediate.ExportCertificatePem());
        }
        byte[] certificates = Encoding.ASCII.GetBytes(pem.ToString());

        long certificatesOffset = payloadLength;
        long signatureOffset = certificatesOffset + certificates.Length;

        var footer = new Footer(
            payloadLength, payloadHash, signatureOffset, signature.Length, certificatesOffset, certificates.Length);

        await using (var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(certificates, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(signature, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(Encode(footer), cancellationToken).ConfigureAwait(false);
        }

        return footer;
    }

    /// <summary>Read a footer, without judging it.</summary>
    /// <exception cref="BundleException">The file is not a <c>.tdi</c>.</exception>
    public static Footer ReadFooter(string imagePath)
    {
        ArgumentNullException.ThrowIfNull(imagePath);

        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < FooterLength)
        {
            throw new BundleException(BundleFailure.MalformedImage, $"{imagePath} is too short to be a .tdi");
        }

        var footer = new byte[FooterLength];
        stream.Seek(-FooterLength, SeekOrigin.End);
        stream.ReadExactly(footer);

        if (!footer.AsSpan(88, 8).SequenceEqual(Magic))
        {
            throw new BundleException(
                BundleFailure.MalformedImage,
                $"{Path.GetFileName(imagePath)} does not end in a Trinix distribution-image footer");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(80));
        if (version != FormatVersion)
        {
            throw new BundleException(
                BundleFailure.MalformedImage,
                $"{Path.GetFileName(imagePath)} is format version {version}; this system understands {FormatVersion}");
        }

        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(footer);
        byte[] payloadHash = footer[8..40];
        long signatureOffset = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(40));
        int signatureLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(48));
        int certificatesLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(52));
        long certificatesOffset = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(56));

        // Every offset is used to seek into a file the caller did not write, so
        // each is bounded before it is believed.
        long declaredEnd = stream.Length - FooterLength;
        bool sane = payloadLength > 0 && payloadLength <= declaredEnd
            && signatureLength is > 0 and < 64 * 1024
            && certificatesLength is > 0 and < 1024 * 1024
            && certificatesOffset >= payloadLength && certificatesOffset + certificatesLength <= declaredEnd
            && signatureOffset >= payloadLength && signatureOffset + signatureLength <= declaredEnd;

        if (!sane)
        {
            throw new BundleException(
                BundleFailure.MalformedImage,
                $"{Path.GetFileName(imagePath)} has a footer whose offsets do not fit the file");
        }

        return new Footer(payloadLength, payloadHash, signatureOffset, signatureLength, certificatesOffset, certificatesLength);
    }

    /// <summary>
    /// Check that a <c>.tdi</c> is whole and was published by someone this
    /// system trusts. Called <b>before</b> the image is mounted.
    /// </summary>
    public static async Task<VerificationResult> VerifyAsync(
        string imagePath,
        TrustStore trust,
        DateTimeOffset? verificationTime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(trust);

        Footer footer;
        try
        {
            footer = ReadFooter(imagePath);
        }
        catch (BundleException e)
        {
            return new VerificationResult(e.Failure, e.Message, null, null, null, null, null);
        }

        byte[] signature;
        byte[] certificateBytes;
        await using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            signature = new byte[footer.SignatureLength];
            stream.Seek(footer.SignatureOffset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(signature, cancellationToken).ConfigureAwait(false);

            certificateBytes = new byte[footer.CertificatesLength];
            stream.Seek(footer.CertificatesOffset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(certificateBytes, cancellationToken).ConfigureAwait(false);
        }

        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(Encoding.ASCII.GetString(certificateBytes));
        }
        catch (CryptographicException e)
        {
            return new VerificationResult(BundleFailure.MalformedImage,
                $"the image's certificate chain is unreadable: {e.Message}", null, null, null, null, null);
        }

        if (certificates.Count == 0)
        {
            return new VerificationResult(BundleFailure.MalformedImage,
                "the image carries no certificates", null, null, null, null, null);
        }

        X509Certificate2 signer = certificates[0];
        string thumbprint = Hex.Of(signer.GetCertHash(HashAlgorithmName.SHA256));

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trust.Roots);
        chain.ChainPolicy.VerificationTime = (verificationTime ?? DateTimeOffset.UtcNow).UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(BundleLayout.CodeSigningEku));
        for (int i = 1; i < certificates.Count; i++) { chain.ChainPolicy.ExtraStore.Add(certificates[i]); }

        if (!chain.Build(signer))
        {
            string reasons = string.Join("; ", chain.ChainStatus.Select(s => s.Status.ToString()));
            return new VerificationResult(BundleFailure.UntrustedSigner,
                $"the image was published by '{signer.Subject}', which does not chain to a trusted root ({reasons})",
                null, null, signer.Subject, thumbprint, null);
        }

        string anchor = chain.ChainElements[^1].Certificate.Subject;

        // The hash is computed over the declared payload length rather than the
        // whole file, which is the point of recording the length: appending to a
        // signed image must not be able to change what the signature covers.
        byte[] actualHash = await HashPrefixAsync(imagePath, footer.PayloadLength, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, footer.PayloadSha256))
        {
            return new VerificationResult(BundleFailure.ContentMismatch,
                "the image's contents do not match the hash in its footer", null, null, signer.Subject, thumbprint, anchor);
        }

        using (ECDsa? publicKey = signer.GetECDsaPublicKey())
        {
            byte[] descriptor = SignedDescriptor(footer.PayloadLength, footer.PayloadSha256);
            bool valid = false;
            try
            {
                valid = publicKey?.VerifyData(
                    descriptor, signature, HashAlgorithmName.SHA256, BundleSealer.SignatureFormat) ?? false;
            }
            catch (CryptographicException) { /* malformed DER: not valid */ }

            if (!valid)
            {
                return new VerificationResult(BundleFailure.BadSignature,
                    "the image's signature is not valid", null, null, signer.Subject, thumbprint, anchor);
            }
        }

        return new VerificationResult(BundleFailure.None,
            $"{Path.GetFileName(imagePath)} — {footer.PayloadLength / 1024} KiB, published by {signer.Subject}",
            null, null, signer.Subject, thumbprint, anchor);
    }

    private static byte[] Encode(Footer footer)
    {
        var bytes = new byte[FooterLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, footer.PayloadLength);
        footer.PayloadSha256.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), footer.SignatureOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), footer.SignatureLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52), footer.CertificatesLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56), footer.CertificatesOffset);
        // 64..80 reserved, left zero.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), FooterLength);
        Magic.CopyTo(bytes.AsSpan(88));
        return bytes;
    }

    private static async Task<byte[]> HashPrefixAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[1 << 16];
        long remaining = length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            sha.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }
        return sha.GetHashAndReset();
    }

    // A stable UUID for a stable name, so that rebuilding the same application
    // twice produces the same filesystem identity. Version 4 bits are set
    // because tools display the version and a zero there reads as corruption.
    private static Guid DeriveUuid(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("trinix-tdi/" + name));
        byte[] uuid = hash[..16];
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x40);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(target, File.GetUnixFileMode(file)); }
        }
    }

    private static async Task RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }

        using Process process = Process.Start(start)
            ?? throw new BundleException(BundleFailure.MalformedImage, $"could not start {program}");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            string message = (await stderr.ConfigureAwait(false)).Trim();
            if (message.Length == 0) { message = (await stdout.ConfigureAwait(false)).Trim(); }
            throw new BundleException(
                BundleFailure.MalformedImage, $"{program} exited with {process.ExitCode}: {message}");
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Trinix.Bundle;

/// <summary>
/// Installing a <c>.tdi</c>: mount it, check what is inside, copy it out.
/// </summary>
/// <remarks>
/// <para>
/// The mount-then-copy shape is deliberate and is the same one macOS has. An
/// installed application is a plain directory on a writable filesystem, not a
/// mounted image, because everything else about the system — the launcher,
/// fs-verity, a future updater — is simpler against a directory, and because an
/// application that stops working when a mount goes away is a support problem
/// that never ends.
/// </para>
/// <para>
/// The order of the two verifications matters. The image signature is checked
/// <b>before</b> the mount, because mounting is the moment attacker-controlled
/// bytes reach a kernel filesystem driver, and that driver is a much larger
/// attack surface than anything in this file. The bundle signature is checked
/// after, from the read-only mount, so that what is verified is what is copied.
/// </para>
/// </remarks>
public static class BundleInstaller
{
    /// <summary>Where installed applications live.</summary>
    public const string ApplicationsDirectory = "/Applications";

    /// <summary>Where install receipts are kept.</summary>
    public const string ReceiptDirectory = "/var/lib/trinix/bundles";

    /// <summary>Where distribution images are mounted while being installed.</summary>
    public const string MountRoot = "/run/trinix/images";

    /// <summary>What an install did.</summary>
    /// <param name="Receipt">The record written to <see cref="ReceiptDirectory"/>.</param>
    /// <param name="Verification">The bundle verification that allowed it.</param>
    public readonly record struct InstallResult(InstallReceipt Receipt, VerificationResult Verification);

    /// <summary>
    /// Install the application inside <paramref name="imagePath"/>.
    /// </summary>
    /// <param name="imagePath">A <c>.tdi</c>.</param>
    /// <param name="trust">The roots to accept.</param>
    /// <param name="destinationDirectory">Where to put it. Defaults to <c>/Applications</c>.</param>
    /// <param name="enableFsVerity">Ask the kernel to seal the installed files.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<InstallResult> InstallAsync(
        string imagePath,
        TrustStore trust,
        string? destinationDirectory = null,
        bool enableFsVerity = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        ArgumentNullException.ThrowIfNull(trust);

        if (!Environment.IsPrivilegedProcess)
        {
            throw new BundleException(
                BundleFailure.NotPermitted,
                "installing needs root: mounting a distribution image is a privileged operation");
        }

        VerificationResult image = await DistributionImage
            .VerifyAsync(imagePath, trust, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!image.Ok)
        {
            throw new BundleException(image.Failure, $"{Path.GetFileName(imagePath)}: {image.Message}");
        }

        string destination = destinationDirectory ?? ApplicationsDirectory;
        Directory.CreateDirectory(destination);

        string mountPoint = Path.Combine(
            MountRoot, Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(mountPoint);

        try
        {
            // ro and nodev/nosuid/noexec: nothing is executed from the image
            // itself, only copied out of it, so the mount has no reason to allow
            // any of the three.
            await RunAsync("mount",
                ["-t", "erofs", "-o", "ro,loop,nodev,nosuid,noexec", imagePath, mountPoint],
                cancellationToken).ConfigureAwait(false);

            string[] bundles = Directory
                .EnumerateDirectories(mountPoint, "*" + BundleLayout.Extension)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (bundles.Length != 1)
            {
                throw new BundleException(
                    BundleFailure.MalformedImage,
                    $"a distribution image must contain exactly one {BundleLayout.Extension} bundle; this one has {bundles.Length}");
            }

            string source = bundles[0];
            VerificationResult bundle = await BundleVerifier
                .VerifyAsync(source, trust, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!bundle.Ok)
            {
                throw new BundleException(bundle.Failure, $"{Path.GetFileName(source)}: {bundle.Message}");
            }

            string target = Path.Combine(destination, Path.GetFileName(source));

            // Staged next to the destination and renamed into place, so that an
            // interrupted install leaves either the old application or none —
            // never half of a new one. Rename is atomic within a filesystem,
            // which is why the staging directory is a sibling rather than in
            // /tmp.
            string staged = target + ".installing";
            if (Directory.Exists(staged)) { Directory.Delete(staged, recursive: true); }
            CopyTree(source, staged);

            if (Directory.Exists(target)) { Directory.Delete(target, recursive: true); }
            Directory.Move(staged, target);

            FsVerityReport? verity = enableFsVerity ? Seal(target) : null;

            var receipt = new InstallReceipt
            {
                Identifier = bundle.Info!.Identifier,
                Version = bundle.Info.Version,
                Path = target,
                MerkleRoot = bundle.Manifest!.MerkleRoot,
                SignerThumbprint = bundle.SignerThumbprint!,
                SignerSubject = bundle.SignerSubject!,
                InstalledAt = DateTimeOffset.UtcNow,
                FsVerity = verity,
            };

            Directory.CreateDirectory(ReceiptDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(ReceiptDirectory, receipt.Identifier + ".json"),
                BundleJson.ToBytes(receipt, BundleJson.Default.InstallReceipt),
                cancellationToken).ConfigureAwait(false);

            return new InstallResult(receipt, bundle);
        }
        finally
        {
            // Best effort: a failed unmount must not mask the error that caused
            // the install to fail, and a leaked mount point under /run does not
            // survive a reboot.
            try
            {
                await RunAsync("umount", [mountPoint], CancellationToken.None).ConfigureAwait(false);
                Directory.Delete(mountPoint);
            }
            catch (BundleException) { /* reported by the caller's own failure, if any */ }
            catch (IOException) { }
        }
    }

    /// <summary>Remove an installed application and its receipt.</summary>
    public static void Uninstall(string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);

        if (!Directory.Exists(bundlePath))
        {
            throw new BundleException(BundleFailure.NotABundle, $"{bundlePath} is not installed");
        }

        string? identifier = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(bundlePath, BundleLayout.InfoPath));
            identifier = JsonSerializer.Deserialize(bytes, BundleJson.Default.BundleInfo)?.Identifier;
        }
        catch (IOException) { /* a bundle too damaged to read is still one to remove */ }
        catch (JsonException) { }

        Directory.Delete(bundlePath, recursive: true);

        if (identifier is not null)
        {
            string receipt = Path.Combine(ReceiptDirectory, identifier + ".json");
            if (File.Exists(receipt)) { File.Delete(receipt); }
        }
    }

    /// <summary>Every receipt on the system, newest install first.</summary>
    public static IEnumerable<InstallReceipt> Installed()
    {
        if (!Directory.Exists(ReceiptDirectory)) { yield break; }

        foreach (string file in Directory.EnumerateFiles(ReceiptDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            InstallReceipt? receipt = null;
            try
            {
                receipt = JsonSerializer.Deserialize(File.ReadAllBytes(file), BundleJson.Default.InstallReceipt);
            }
            catch (IOException) { }
            catch (JsonException) { }

            if (receipt is not null) { yield return receipt; }
        }
    }

    private static FsVerityReport Seal(string bundlePath)
    {
        int enabled = 0, skipped = 0;
        string? reason = null;

        foreach (ScannedFile file in BundleScanner.Scan(bundlePath))
        {
            if (FsVerity.TryEnable(file.FullPath, out string? why))
            {
                enabled++;
            }
            else
            {
                skipped++;
                reason ??= why;
            }
        }

        return new FsVerityReport { Enabled = enabled, Skipped = skipped, Reason = reason };
    }

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

            // The execute bit is signed over, so it has to survive the copy.
            // The rest of the mode is not: an installed application is
            // world-readable and owned by root, which is what the read-only
            // system's own files are.
            if (!OperatingSystem.IsWindows())
            {
                UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                if (File.GetUnixFileMode(file).HasFlag(UnixFileMode.UserExecute))
                {
                    mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                }
                File.SetUnixFileMode(target, mode);
            }
        }
    }

    private static async Task RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }

        using Process process = Process.Start(start)
            ?? throw new BundleException(BundleFailure.NotPermitted, $"could not start {program}");

        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new BundleException(
                BundleFailure.MalformedImage,
                $"{program} exited with {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        }
    }
}

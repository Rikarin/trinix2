using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Trinix.Bundle;

/// <summary>
///     fs-verity: the kernel's own answer to "has this file changed since it was
///     installed".
/// </summary>
/// <remarks>
///     <para>
///         Enabling verity on a file makes it permanently read-only and gives the
///         kernel a Merkle tree over its contents, which it checks page by page as the
///         file is read. That is a different kind of guarantee from the one
///         <see cref="BundleVerifier" /> provides. The signed manifest proves that a
///         bundle was unmodified <i>at the moment it was checked</i>; fs-verity means
///         a modification cannot happen at all, including between the check and the
///         <c>execve</c> — the window that a determined attacker with write access
///         would aim for.
///     </para>
///     <para>
///         It is best-effort on purpose. The filesystem has to have been created with
///         the feature (Trinix's <c>/data</c> is, see
///         <c>image/scripts/build-image.sh</c>), the kernel has to have
///         <c>CONFIG_FS_VERITY</c>, and neither is true of, say, a bundle a developer
///         is running out of a build directory over a bind mount. Nothing in the launch
///         path depends on verity being on; when it is not, the full hash walk is what
///         stands in its place, and the install receipt records which one happened
///         rather than leaving it to be guessed.
///     </para>
/// </remarks>
public static partial class FsVerity {
    // _IOW('f', 133, struct fsverity_enable_arg) under the asm-generic ioctl
    // encoding, which is what both arm64 and x86_64 use:
    //   (write << 30) | (sizeof(arg) << 16) | ('f' << 8) | 133
    const ulong EnableVerity = 0x4080_6685;

    // _IOR('f', 1, long) — FS_IOC_GETFLAGS, the same call lsattr makes.
    const ulong GetFlags = 0x8008_6601;

    const int VerityFlag = 0x0010_0000; // FS_VERITY_FL

    const uint HashAlgorithmSha256 = 1;

    const int Enotty = 25; // the ioctl is not implemented here
    const int Eopnotsupp = 95; // the filesystem does not support verity

    /// <summary>
    ///     Turn on fs-verity for one file.
    /// </summary>
    /// <returns><see langword="true" /> if the file is now protected.</returns>
    /// <param name="path">The file. Must not be open for writing anywhere.</param>
    /// <param name="reason">Why not, when the answer is no.</param>
    public static bool TryEnable(string path, out string? reason) {
        if (!OperatingSystem.IsLinux()) {
            reason = "fs-verity is a Linux feature";
            return false;
        }

        // Already on is success, not failure: enabling twice returns EEXIST and
        // the caller's question is "is this file protected", not "did I protect
        // it".
        if (IsEnabled(path)) {
            reason = null;
            return true;
        }

        SafeFileHandle handle;
        try {
            // Read-only, as the ioctl requires: the kernel refuses to seal a
            // file that anyone — including the caller — could still write.
            handle = File.OpenHandle(path);
        } catch (IOException e) {
            reason = e.Message;
            return false;
        } catch (UnauthorizedAccessException e) {
            reason = e.Message;
            return false;
        }

        using (handle) {
            var arg = new EnableArg { Version = 1, HashAlgorithm = HashAlgorithmSha256, BlockSize = 4096 };

            if (IoctlEnable(handle, EnableVerity, ref arg) == 0) {
                reason = null;
                return true;
            }

            var errno = Marshal.GetLastPInvokeError();
            reason = errno switch {
                Eopnotsupp => "the filesystem was not created with the verity feature",
                Enotty => "this kernel has no fs-verity support",
                _ => new Win32Exception(errno).Message
            };
            return false;
        }
    }

    /// <summary>Is fs-verity already on for this file?</summary>
    public static bool IsEnabled(string path) {
        if (!OperatingSystem.IsLinux()) {
            return false;
        }

        try {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var flags = 0;
            if (IoctlGetFlags(handle, GetFlags, ref flags) != 0) {
                return false;
            }

            return (flags & VerityFlag) != 0;
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlEnable(SafeFileHandle fd, ulong request, ref EnableArg arg);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlGetFlags(SafeFileHandle fd, ulong request, ref int flags);

    /// <summary>
    ///     The kernel's <c>struct fsverity_enable_arg</c>.
    /// </summary>
    /// <remarks>
    ///     The reserved tail is part of the ABI: the ioctl number encodes the
    ///     structure's size, so a struct of the wrong length is not a truncated
    ///     argument — it is a different ioctl, and the kernel rejects it with
    ///     ENOTTY.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    struct EnableArg {
        public uint Version;
        public uint HashAlgorithm;
        public uint BlockSize;
        public uint SaltSize;
        public ulong SaltPtr;
        public uint SignatureSize;
        public uint Reserved1;
        public ulong SignaturePtr;
        public unsafe fixed ulong Reserved2[11];
    }
}

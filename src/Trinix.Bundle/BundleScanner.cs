namespace Trinix.Bundle;

/// <summary>One file found on disk inside a bundle.</summary>
/// <param name="RelativePath">Bundle-relative, forward slashes.</param>
/// <param name="FullPath">Where it actually is.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="Executable">Whether the owner's execute bit is set.</param>
public readonly record struct ScannedFile(string RelativePath, string FullPath, long Size, bool Executable);

/// <summary>
///     Walks a bundle directory, in the one order everything else agrees on.
/// </summary>
/// <remarks>
///     <para>
///         Hand-written rather than <c>Directory.EnumerateFiles(..., AllDirectories)</c>
///         for two reasons that both come down to trust. The recursion has to refuse to
///         follow a symlinked directory — otherwise a bundle containing a link to
///         <c>/</c> would be "signed" over the whole filesystem, and the signature
///         would take a week to compute and mean nothing. And the ordering has to be
///         ordinal and explicit, because it is an input to the Merkle root: a walk that
///         depended on the filesystem's readdir order would produce a different root on
///         ext4 and on erofs for the same bundle.
///     </para>
///     <para>
///         A symlink is refused rather than skipped. Skipping would leave it outside
///         the signature — which is precisely where an attacker would want to put
///         something. Device nodes, sockets and fifos are not distinguishable through
///         .NET's filesystem API and are not defended against here; the defence against
///         those is that a bundle is produced by <c>trinix-bundle seal</c> from a
///         publish directory, and one that appeared later would fail the size and hash
///         comparison at verification time.
///     </para>
/// </remarks>
public static class BundleScanner {
    /// <summary>
    ///     Every regular file in the bundle except its signature material, ordered
    ///     ordinally by relative path.
    /// </summary>
    /// <exception cref="BundleException">
    ///     The tree contains a symlink, a device node, a socket or a fifo.
    /// </exception>
    public static IReadOnlyList<ScannedFile> Scan(string bundlePath) {
        ArgumentNullException.ThrowIfNull(bundlePath);

        var root = Path.GetFullPath(bundlePath);
        List<ScannedFile> found = [];
        Stack<string> pending = new();
        pending.Push(root);

        while (pending.Count > 0) {
            var directory = pending.Pop();

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) {
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');

                if (BundleLayout.IsSignatureMaterial(relative)) {
                    continue;
                }

                var info = new FileInfo(entry);

                if (info.LinkTarget is not null) {
                    throw new BundleException(
                        BundleFailure.UnsupportedEntry,
                        $"{relative} is a symbolic link; a bundle must be a self-contained tree of regular files"
                    );
                }

                if ((info.Attributes & FileAttributes.Directory) != 0) {
                    pending.Push(entry);
                    continue;
                }

                found.Add(new(relative, entry, info.Length, IsExecutable(entry)));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return found;
    }

    /// <summary>Is the owner's execute bit set?</summary>
    /// <remarks>
    ///     Only the owner's bit is recorded. A bundle is installed by copying, and
    ///     the copy's group and other bits follow the installing process's umask
    ///     rather than the source — so recording them would make the signature
    ///     depend on the umask of whoever built the image.
    /// </remarks>
    public static bool IsExecutable(string path) {
        if (OperatingSystem.IsWindows()) {
            return false;
        }

        return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
    }
}

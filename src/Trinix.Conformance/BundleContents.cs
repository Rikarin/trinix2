using Trinix.Bundle;

namespace Trinix.Conformance;

/// <summary>One entry found in a bundle, whatever kind of thing it is.</summary>
/// <param name="RelativePath">Bundle-relative, forward slashes.</param>
/// <param name="FullPath">Where it actually is.</param>
/// <param name="Kind">File, directory or symlink.</param>
/// <param name="LinkTarget">What the symlink says, verbatim.</param>
/// <param name="Escapes">Whether following the link leaves the bundle.</param>
/// <param name="Size">Length in bytes, for a regular file.</param>
/// <param name="Mode">The Unix mode bits, or <see langword="null" /> off Unix.</param>
/// <param name="ChildCount">How many entries a directory contains.</param>
sealed record BundleEntry(
    string RelativePath,
    string FullPath,
    EntryKind Kind,
    string? LinkTarget,
    bool Escapes,
    long Size,
    UnixFileMode? Mode,
    int ChildCount
);

/// <summary>What one entry in a bundle is.</summary>
enum EntryKind {
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link, to anything.</summary>
    Symlink
}

/// <summary>
///     Walks a bundle the way <see cref="BundleScanner" /> does, and does not refuse.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A second walk, deliberately, and the difference is the whole reason
///         doctor exists.</b> <see cref="BundleScanner" /> throws on the first symlink
///         it meets, which is exactly right for a sealer — a bundle that cannot be
///         signed honestly must not be signed at all — and exactly wrong for a tool
///         whose job is to tell a developer everything that is wrong in one pass. A
///         bundle with four symlinks would otherwise take four runs and four fixes to
///         get a fifth error out of.
///     </para>
///     <para>
///         It also sees what the scanner has no reason to record: directories, so an
///         empty one can be reported before it silently vanishes from a signed file
///         list; the group and other mode bits, which the manifest deliberately does
///         not cover; and where a link points, so "a symlink" can be told from "a
///         symlink out of the bundle", which are a hygiene problem and a security
///         problem respectively.
///     </para>
///     <para>
///         ⚠ It does not follow links, for the reason the scanner gives: a bundle
///         containing a link to <c>/</c> would otherwise take a week to walk.
///     </para>
/// </remarks>
static class BundleContents {
    /// <summary>
    ///     Everything in the bundle, ordered ordinally by relative path.
    /// </summary>
    /// <param name="bundlePath">The <c>.app</c> directory.</param>
    /// <param name="problem">
    ///     What stopped the walk part-way, or <see langword="null" />. Whatever was
    ///     found before that is still returned.
    /// </param>
    internal static IReadOnlyList<BundleEntry> Walk(string bundlePath, out string? problem) {
        problem = null;
        var root = Path.GetFullPath(bundlePath);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        List<BundleEntry> found = [];
        Stack<string> pending = new();
        pending.Push(root);

        while (pending.Count > 0) {
            var directory = pending.Pop();

            string[] entries;
            try {
                entries = [.. Directory.EnumerateFileSystemEntries(directory)];
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                problem = e.Message;
                continue;
            }

            foreach (var entry in entries) {
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');

                FileInfo info;
                try {
                    info = new FileInfo(entry);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    problem = e.Message;
                    continue;
                }

                // Asked before the directory bit, exactly as BundleScanner asks it: a
                // symlink to a directory reports FileAttributes.Directory, and
                // recursing into it is how a bundle gets to claim the filesystem.
                if (info.LinkTarget is { } target) {
                    found.Add(new BundleEntry(
                        relative,
                        entry,
                        EntryKind.Symlink,
                        target,
                        Escapes(prefix, entry, target),
                        0,
                        null,
                        0
                    ));

                    continue;
                }

                if ((info.Attributes & FileAttributes.Directory) != 0) {
                    pending.Push(entry);

                    var children = 0;
                    try {
                        children = Directory.EnumerateFileSystemEntries(entry).Count();
                    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                        problem = e.Message;
                    }

                    found.Add(new BundleEntry(relative, entry, EntryKind.Directory, null, false, 0, null, children));
                    continue;
                }

                found.Add(new BundleEntry(
                    relative,
                    entry,
                    EntryKind.File,
                    null,
                    false,
                    info.Length,
                    ModeOf(entry),
                    0
                ));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return found;
    }

    /// <summary>Does following this link leave the bundle?</summary>
    /// <remarks>
    ///     ⚠ Textual, against the link's own directory, and never
    ///     <c>ResolveLinkTarget</c>. Resolving would follow the link — which on a
    ///     hostile bundle is the operation being defended against — and would answer
    ///     <see langword="null" /> for a dangling link, which is the case that most
    ///     needs an answer.
    /// </remarks>
    static bool Escapes(string bundlePrefix, string linkPath, string target) {
        try {
            var resolved = Path.GetFullPath(target, Path.GetDirectoryName(linkPath) ?? bundlePrefix);
            return !resolved.StartsWith(bundlePrefix, StringComparison.Ordinal);
        } catch (Exception e) when (e is ArgumentException or PathTooLongException) {
            // A target this malformed is not one to reassure anybody about.
            return true;
        }
    }

    static UnixFileMode? ModeOf(string path) {
        if (OperatingSystem.IsWindows()) {
            return null;
        }

        try {
            return File.GetUnixFileMode(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}

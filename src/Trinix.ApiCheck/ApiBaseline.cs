using System.Text;

namespace Trinix.ApiCheck;

/// <summary>
///     The committed record of what an assembly's public surface is allowed to be, and the
///     comparison of a reading against it.
/// </summary>
/// <remarks>
///     <para>
///         Two files per project, both beside its <c>.csproj</c>:
///         <c>PublicAPI.Shipped.txt</c> is what a released Trinix published and is never rewritten
///         by this tool; <c>PublicAPI.Unshipped.txt</c> is everything approved since, including
///         <c>*REMOVED*</c> lines for shipped API that has been taken away. At a release the second
///         is folded into the first.
///     </para>
///     <para>
///         The split is the point rather than bookkeeping. It is what turns "this release removed
///         something a caller was using" into a reviewed line in a file instead of an absence
///         nobody looked for — and a gate that only catches additions is a gate that lets the
///         breaking change through, which is the one it exists to stop.
///     </para>
///     <para>
///         Trinix has released nothing, so every <c>Shipped</c> file is empty and honest. Writing
///         the current surface into it would claim a compatibility promise that has not been made.
///     </para>
/// </remarks>
static class ApiBaseline {
    internal const string ShippedFileName = "PublicAPI.Shipped.txt";
    internal const string UnshippedFileName = "PublicAPI.Unshipped.txt";

    /// <summary>Marks a shipped entry that has since been removed.</summary>
    internal const string RemovedPrefix = "*REMOVED*";

    /// <summary>
    ///     Written as the first line of every baseline. The entries carry <c>?</c> and <c>!</c>
    ///     nullability annotations and this is what says so; the file is read by people at least as
    ///     often as by this tool.
    /// </summary>
    const string Header = "#nullable enable";

    /// <summary>
    ///     Where the baselines for a project live: beside its <c>.csproj</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Told rather than derived, which is the one place this tool departs from Vixen's.
    ///     Vixen walks up from the build output until it meets a <c>.csproj</c>, because its
    ///     assemblies land in <c>&lt;project&gt;/bin/</c>. Trinix sets <c>ArtifactsPath</c> in
    ///     <c>src/Directory.Build.props</c>, so an assembly lands in <c>out/dotnet/bin/</c> and
    ///     that walk reaches the repository root having found nothing. Pairing is therefore the
    ///     caller's job — and it is checked, in <c>Program.cs</c>, rather than assumed.
    /// </remarks>
    internal static string DirectoryFor(string projectPath) =>
        Path.GetDirectoryName(Path.GetFullPath(projectPath))
        ?? throw new InvalidOperationException($"'{projectPath}' has no directory to hold an API baseline.");

    /// <summary>
    ///     Reads a baseline file, or an empty list when there is none. Blank lines and comments are
    ///     not entries.
    /// </summary>
    internal static IReadOnlyList<string> Read(string path) =>
        File.Exists(path)
            ? [
                .. File.ReadAllLines(path)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#'))
            ]
            : [];

    /// <summary>Writes a baseline file, sorted, with the header and Unix line endings.</summary>
    /// <remarks>
    ///     The line ending is fixed rather than the platform's. These files are regenerated on
    ///     whichever operating system the developer running <c>-Update</c> happens to have, and a
    ///     baseline that rewrites every line when it is regenerated on Windows is a baseline whose
    ///     diffs say nothing. <c>.editorconfig</c> says <c>end_of_line = lf</c> for the same reason.
    /// </remarks>
    internal static void Write(string path, IEnumerable<string> entries) {
        var content = new StringBuilder().Append(Header).Append('\n');

        foreach (var entry in entries.OrderBy(entry => entry, StringComparer.Ordinal)) {
            content.Append(entry).Append('\n');
        }

        File.WriteAllText(path, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    ///     The surface a caller has been promised: everything shipped, plus everything approved
    ///     since, minus everything a <c>*REMOVED*</c> line has withdrawn.
    /// </summary>
    internal static IReadOnlySet<string> Approved(IEnumerable<string> shipped, IEnumerable<string> unshipped) {
        var approved = new HashSet<string>(shipped, StringComparer.Ordinal);

        foreach (var entry in unshipped) {
            if (entry.StartsWith(RemovedPrefix, StringComparison.Ordinal)) {
                approved.Remove(entry[RemovedPrefix.Length..]);
            } else {
                approved.Add(entry);
            }
        }

        return approved;
    }

    /// <summary>Compares a reading of the surface against the baseline that approves it.</summary>
    internal static ApiDifference Compare(
        IReadOnlyList<string> surface,
        IEnumerable<string> shipped,
        IEnumerable<string> unshipped
    ) {
        var approved = Approved(shipped, unshipped);
        var present = new HashSet<string>(surface, StringComparer.Ordinal);

        return new(
            [.. surface.Where(entry => !approved.Contains(entry)).OrderBy(entry => entry, StringComparer.Ordinal)],
            [.. approved.Where(entry => !present.Contains(entry)).OrderBy(entry => entry, StringComparer.Ordinal)]
        );
    }

    /// <summary>
    ///     The contents <c>PublicAPI.Unshipped.txt</c> should have for this surface: everything not
    ///     already shipped, and a <c>*REMOVED*</c> line for everything shipped that is gone.
    /// </summary>
    internal static IReadOnlyList<string> Rebase(IReadOnlyList<string> surface, IEnumerable<string> shipped) {
        var released = new HashSet<string>(shipped, StringComparer.Ordinal);
        var present = new HashSet<string>(surface, StringComparer.Ordinal);

        var unshipped = new SortedSet<string>(surface.Where(entry => !released.Contains(entry)), StringComparer.Ordinal);

        foreach (var gone in released.Where(entry => !present.Contains(entry))) {
            unshipped.Add(RemovedPrefix + gone);
        }

        return [.. unshipped];
    }
}

/// <summary>What a reading of the surface has that the baseline does not, and the reverse.</summary>
/// <param name="Added">Entries in the assembly that no baseline approves — an unapproved addition.</param>
/// <param name="Removed">Entries the baseline approves that the assembly no longer has — a break.</param>
sealed record ApiDifference(IReadOnlyList<string> Added, IReadOnlyList<string> Removed) {
    internal bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}

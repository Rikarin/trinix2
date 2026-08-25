using Trinix.Bundle;

namespace Trinix.Conformance;

/// <summary>
///     Bundle hygiene: the tree, its modes, and the things that should not be in it.
/// </summary>
/// <remarks>
///     <para>
///         Most of what is here is already refused somewhere. <see cref="BundleScanner" />
///         throws on a symlink, <see cref="BundleSealer" /> refuses a missing or
///         non-executable entry point, and the verifier notices a file that is not in
///         the manifest. Doctor's contribution is timing and completeness: it says all
///         of it at once, before anything has been signed, and it says which of the
///         four symlinks is the one pointing out of the bundle.
///     </para>
///     <para>
///         ⚠ The mode-bit check is the one place doctor knows something the signature
///         does not. <see cref="BundleScanner" /> records only the owner's execute bit,
///         for a good reason — the group and other bits follow the installing process's
///         umask, so recording them would make the Merkle root depend on whoever ran
///         the build. The consequence is that a group-writable file inside a bundle is
///         invisible to every check that runs after sealing, and doc 01's "nothing in
///         the bundle is writable" has nowhere else to be enforced.
///     </para>
/// </remarks>
static class ContentsChecks {
    /// <summary>
    ///     Files a build leaves behind that a bundle should not carry.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>.DS_Store</c> is first because Trinix is developed on a Mac and the
    ///     Finder writes one into any directory somebody opens. It is inside the
    ///     signature once sealed, so it is not merely untidy: it is a file whose
    ///     contents depend on how a window was scrolled, shipped to users, and enough
    ///     on its own to make two builds of the same source disagree.
    /// </remarks>
    static readonly (string Pattern, string Reason)[] Stray = [
        (".DS_Store", "the Finder writes it into any directory it is shown, and sealing puts it "
            + "inside the signature — where it makes two builds of the same source disagree"),
        ("Thumbs.db", "a Windows shell cache; the same argument as .DS_Store"),
        ("*.pdb", "debug symbols. src/pack-apps.sh deletes them for a reason: whether the SDK "
            + "emitted one is not a property of the application, so a bundle that carries them has "
            + "a file list that depends on the build configuration"),
        ("appsettings.Development.json", "a development configuration file, shipped")
    ];

    internal static void Run(
        DoctorContext context,
        BundleInfo info,
        string bundlePath,
        IReadOnlyList<BundleEntry> entries,
        string? walkProblem
    ) {
        context.Ran("contents.readable");
        if (walkProblem is not null) {
            context.Warn(
                "contents.readable",
                $"part of the bundle could not be read: {walkProblem}",
                "every check below saw less than the whole tree, so a clean result from them means "
                + "less than it looks",
                "fix the permissions on the bundle, or run doctor as the user that built it",
                where: null
            );
        }

        Structure(context, bundlePath, entries);
        Symlinks(context, entries);
        Writable(context, entries);
        EntryPoint(context, info, entries);
        EmptyDirectories(context, entries);
        StrayFiles(context, entries);
        ExecutableResources(context, entries);
        Icon(context, entries);
    }

    static void Structure(DoctorContext context, string bundlePath, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.extension");
        if (!bundlePath.TrimEnd('/').EndsWith(BundleLayout.Extension, StringComparison.Ordinal)) {
            context.Warn(
                "contents.extension",
                $"the bundle directory does not end in '{BundleLayout.Extension}'",
                "the suffix is what makes a directory an application bundle to the shell, the "
                + "installer and `trinix-bundle`; without it this is a directory that happens to "
                + "contain an Info.json",
                $"rename it to '{BundleLayout.NameOf(bundlePath)}{BundleLayout.Extension}'"
            );
        }

        context.Ran("contents.top-level");
        foreach (var entry in entries) {
            if (entry.RelativePath.Contains('/', StringComparison.Ordinal)
                || entry.RelativePath == BundleLayout.Contents) {
                continue;
            }

            context.Warn(
                "contents.top-level",
                $"'{entry.RelativePath}' is at the top of the bundle, outside {BundleLayout.Contents}/",
                "the format has exactly one directory at the top and everything lives under it; the "
                + "sandbox binds the bundle read-only and resolves the entry point relative to it, so "
                + "a file up here is signed, shipped and unreachable by anything",
                $"move it under {BundleLayout.Contents}/, or delete it",
                entry.RelativePath
            );
        }

        context.Ran("contents.signature-material");
        var manifest = entries.Any(e => e.RelativePath == BundleLayout.ManifestPath);
        var signature = entries.Any(e => e.RelativePath == BundleLayout.SignaturePath);
        var certificates = entries.Any(e => e.RelativePath == BundleLayout.CertificatesPath);

        if (manifest || signature || certificates) {
            List<string> missing = [];
            if (!manifest) { missing.Add(BundleLayout.ManifestPath); }
            if (!signature) { missing.Add(BundleLayout.SignaturePath); }
            if (!certificates) { missing.Add(BundleLayout.CertificatesPath); }

            if (missing.Count > 0) {
                context.Error(
                    "contents.signature-material",
                    $"{BundleLayout.SignatureDirectory} is incomplete: {string.Join(", ", missing)} missing",
                    "the verifier reads all three and refuses the bundle as unsigned when any is "
                    + "absent — a half-present signature directory is usually a copy that dropped "
                    + "a file rather than a bundle nobody sealed",
                    "run `trinix-bundle seal` again; sealing removes the old material first, so "
                    + "there is nothing to clean up",
                    BundleLayout.SignatureDirectory
                );
            }
        }
    }

    static void Symlinks(DoctorContext context, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.symlink");

        foreach (var entry in entries) {
            if (entry.Kind != EntryKind.Symlink) {
                continue;
            }

            if (entry.Escapes) {
                context.Error(
                    "contents.symlink",
                    $"'{entry.RelativePath}' is a symbolic link to '{entry.LinkTarget}', outside the bundle",
                    "a bundle is signed as a self-contained tree of regular files. A link out of it "
                    + "either signs the whole filesystem or signs nothing about what will actually be "
                    + "read at that path — `trinix-bundle seal` refuses to sign either",
                    "copy the target into the bundle, or drop the file. If it is a shared library, "
                    + $"it belongs in {BundleLayout.FrameworksDirectory}/",
                    entry.RelativePath
                );

                continue;
            }

            context.Error(
                "contents.symlink",
                $"'{entry.RelativePath}' is a symbolic link to '{entry.LinkTarget}'",
                "the sealer refuses every symlink, including one that stays inside: the link is not a "
                + "regular file, so it has no hash and no size to record, and skipping it would leave "
                + "it outside the signature — which is precisely where an attacker would want it",
                "replace the link with a copy of what it points at",
                entry.RelativePath
            );
        }
    }

    static void Writable(DoctorContext context, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.writable");

        foreach (var entry in entries) {
            if (entry.Mode is not { } mode) {
                continue;
            }

            if (!mode.HasFlag(UnixFileMode.GroupWrite) && !mode.HasFlag(UnixFileMode.OtherWrite)) {
                continue;
            }

            context.Warn(
                "contents.writable",
                $"'{entry.RelativePath}' is writable by "
                + (mode.HasFlag(UnixFileMode.OtherWrite) ? "everyone" : "its group")
                + $" (mode {Octal(mode)})",
                "doc 01 asks that nothing in a bundle be writable, and the signature cannot help "
                + "here: BundleScanner records only the owner's execute bit, so these mode bits are "
                + "outside the Merkle leaf and no check after sealing will ever see them again",
                $"chmod go-w '{entry.RelativePath}' before sealing. ⚠ `trinix-bundle install` "
                + "normalises modes to 0644/0755 on the way in, so this bites hardest on a bundle "
                + "run from where it was built",
                entry.RelativePath
            );
        }
    }

    static void EntryPoint(DoctorContext context, BundleInfo info, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.entry-point.present");

        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.RelativePath, info.EntryPoint, StringComparison.Ordinal)
        );

        if (entry is null || entry.Kind != EntryKind.File) {
            context.Error(
                "contents.entry-point.present",
                $"entryPoint '{info.EntryPoint}' is not a file in the bundle",
                "the sealer refuses a bundle whose entry point is not there, and the unit's "
                + "`Type=exec` means a bad path is a failed launch rather than a unit that was "
                + "briefly alive — but a developer should hear it here, not from systemd",
                entries.Any(e => e.RelativePath.StartsWith(BundleLayout.BinDirectory, StringComparison.Ordinal))
                    ? "set \"entryPoint\" to one of: "
                    + string.Join(", ", entries
                        .Where(e => e.Kind == EntryKind.File
                            && e.RelativePath.StartsWith(BundleLayout.BinDirectory + "/", StringComparison.Ordinal)
                            && (e.Mode?.HasFlag(UnixFileMode.UserExecute) ?? false))
                        .Select(e => "'" + e.RelativePath + "'")
                        .Take(5))
                    : $"put the executable in {BundleLayout.BinDirectory}/ and name it in \"entryPoint\"",
                BundleLayout.InfoPath
            );

            return;
        }

        context.Ran("contents.entry-point.executable");
        if (entry.Mode is { } mode && !mode.HasFlag(UnixFileMode.UserExecute)) {
            context.Error(
                "contents.entry-point.executable",
                $"entryPoint '{info.EntryPoint}' is not executable (mode {Octal(mode)})",
                "the owner's execute bit is the one thing the manifest does record, so this is "
                + "signed as non-executable and stays that way through install; the sealer refuses "
                + "it for exactly that reason",
                $"chmod +x '{info.EntryPoint}' before sealing",
                entry.RelativePath
            );
        }

        context.Ran("contents.entry-point.location");
        if (!info.EntryPoint.StartsWith(BundleLayout.BinDirectory + "/", StringComparison.Ordinal)) {
            context.Warn(
                "contents.entry-point.location",
                $"entryPoint '{info.EntryPoint}' is not under {BundleLayout.BinDirectory}/",
                "it is legal — the launcher reads the path rather than guessing at one — but the "
                + "layout is what tells a reader which files in a bundle are code, and a bundle that "
                + "runs something out of Resources/ is one nobody can audit by looking",
                $"move the executable to {BundleLayout.BinDirectory}/ and update \"entryPoint\"",
                BundleLayout.InfoPath
            );
        }
    }

    static void EmptyDirectories(DoctorContext context, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.empty-directory");

        foreach (var entry in entries) {
            if (entry.Kind != EntryKind.Directory || entry.ChildCount != 0) {
                continue;
            }

            context.Warn(
                "contents.empty-directory",
                $"'{entry.RelativePath}' is empty",
                "the manifest lists files, so an empty directory is not signed, not compared at "
                + "verification and not guaranteed to exist after install — an application that "
                + "expects to open something in it fails on a user's machine and not on yours",
                "delete it, or put the file that belongs in it there. If the application needs a "
                + "writable directory it should create it under its own $HOME, which is its container",
                entry.RelativePath
            );
        }
    }

    static void StrayFiles(DoctorContext context, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.stray");

        foreach (var entry in entries) {
            if (entry.Kind != EntryKind.File) {
                continue;
            }

            var name = FileName(entry.RelativePath);

            foreach (var (pattern, reason) in Stray) {
                var matches = pattern.StartsWith('*')
                    ? name.EndsWith(pattern[1..], StringComparison.Ordinal)
                    : string.Equals(name, pattern, StringComparison.Ordinal);

                if (!matches) {
                    continue;
                }

                context.Warn(
                    "contents.stray",
                    $"'{entry.RelativePath}' should not be in a shipped bundle",
                    reason,
                    $"delete it before sealing (`find … -name '{pattern}' -delete`)",
                    entry.RelativePath
                );

                break;
            }
        }
    }

    static void ExecutableResources(DoctorContext context, IReadOnlyList<BundleEntry> entries) {
        context.Ran("contents.resources.executable");

        foreach (var entry in entries) {
            if (entry.Kind != EntryKind.File
                || !entry.RelativePath.StartsWith(BundleLayout.ResourcesDirectory + "/", StringComparison.Ordinal)
                || !(entry.Mode?.HasFlag(UnixFileMode.UserExecute) ?? false)) {
                continue;
            }

            context.Warn(
                "contents.resources.executable",
                $"'{entry.RelativePath}' is a resource and is executable",
                $"{BundleLayout.ResourcesDirectory}/ is data. The execute bit is inside the Merkle "
                + "leaf, so this ships as a runnable file in the directory nobody reviews for code — "
                + "and the installer widens it to 0755 rather than dropping it",
                $"chmod -x '{entry.RelativePath}', or move it to {BundleLayout.BinDirectory}/ if it "
                + "is meant to be run",
                entry.RelativePath
            );
        }
    }

    static void Icon(DoctorContext context, IReadOnlyList<BundleEntry> entries) {
        // ⚠ Not a check, and it says so. Doc 01 asks doctor to verify "that the icon
        // exists at every size", and Info.json has no icon field, the layout names no
        // icon path, and no size set is written down anywhere. Inventing one here would
        // be defining the format inside the tool that validates it — the first bundle
        // to comply would be complying with a convention that exists in one C# file.
        // What is left that is worth saying is the observation, without the verdict.
        context.Ran("contents.icon");

        var candidates = entries
            .Where(e => e.Kind == EntryKind.File
                && e.RelativePath.StartsWith(BundleLayout.ResourcesDirectory + "/", StringComparison.Ordinal)
                && FileName(e.RelativePath).Contains("icon", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.RelativePath)
            .ToList();

        context.Note(
            "contents.icon",
            candidates.Count == 0
                ? $"nothing under {BundleLayout.ResourcesDirectory}/ looks like an icon"
                : $"possible icons: {string.Join(", ", candidates.Take(4))}",
            "doc 01 asks doctor to check that the icon exists at every size, and it cannot: "
            + "Info.json has no icon field and the bundle format names no path or size set for one",
            "nothing yet — this becomes a real check when the format says what an icon is"
        );
    }

    /// <summary>The last segment of a bundle-relative path.</summary>
    static string FileName(string relativePath) =>
        relativePath[(relativePath.LastIndexOf('/') + 1)..];

    /// <summary>Mode bits as a person reads them: <c>0644</c>.</summary>
    static string Octal(UnixFileMode mode) =>
        "0" + Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0');
}

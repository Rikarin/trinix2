using System.Globalization;
using Trinix.ApiCheck;

// trinix-api-check — the public surface of an assembly, against the baseline committed beside the
// project that produced it.
//
// Arguments come in pairs, a .csproj and the assembly it produced, because src/Directory.Build.props
// sets ArtifactsPath and so an assembly does not live anywhere near its project. Nothing here knows
// *which* projects matter: that is scripts/check-api.ps1's decision, and a decision written down in
// two places is a decision that stops agreeing with itself.

const int ExitOk = 0;
const int ExitDiffers = 1;
const int ExitUsage = 2;

// How many differing entries are printed per assembly before the rest are counted instead. The
// first run against a project with no baseline produces thousands, and a console that scrolls for
// a minute is a console nobody reads the top of.
const int MaxReported = 20;

// How wide a single entry is allowed to be on the console before the rest of it is a
// character count. See Report.
const int MaxEntryWidth = 160;

var update = false;
var fold = false;
var paths = new List<string>();

foreach (var argument in args) {
    switch (argument) {
        case "--update" or "-u":
            update = true;
            break;

        case "--fold" or "-f":
            fold = true;
            break;

        case "--help" or "-h":
            Usage();
            return ExitOk;

        default:
            if (argument.StartsWith('-')) {
                Console.Error.WriteLine($"trinix-api-check: unknown option '{argument}'");
                Usage();

                return ExitUsage;
            }

            paths.Add(argument);
            break;
    }
}

if (paths.Count == 0) {
    Console.Error.WriteLine("trinix-api-check: no projects given");
    Usage();

    return ExitUsage;
}

// The pairing is checked rather than trusted. A caller that got the order wrong would otherwise
// compare one project's surface against another project's baseline and report both as rewritten,
// which is the most confusing way this tool could possibly fail.
if (paths.Count % 2 != 0) {
    Console.Error.WriteLine("trinix-api-check: arguments come in pairs — <project.csproj> <assembly.dll>");

    return ExitUsage;
}

var pairs = new List<(string Project, string Assembly)>(paths.Count / 2);

for (var i = 0; i < paths.Count; i += 2) {
    if (!paths[i].EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
        Console.Error.WriteLine($"trinix-api-check: expected a .csproj, got '{paths[i]}'");

        return ExitUsage;
    }

    if (!File.Exists(paths[i])) {
        Console.Error.WriteLine($"trinix-api-check: there is no project at '{paths[i]}'");

        return ExitUsage;
    }

    pairs.Add((paths[i], paths[i + 1]));
}

var differing = 0;

foreach (var (projectPath, assemblyPath) in pairs) {
    if (!File.Exists(assemblyPath)) {
        Console.Error.WriteLine($"trinix-api-check: there is no assembly at '{assemblyPath}' — build first");

        return ExitUsage;
    }

    var name = Path.GetFileNameWithoutExtension(assemblyPath);
    var directory = ApiBaseline.DirectoryFor(projectPath);
    var shippedPath = Path.Combine(directory, ApiBaseline.ShippedFileName);
    var unshippedPath = Path.Combine(directory, ApiBaseline.UnshippedFileName);

    var surface = ApiSurfaceReader.Read(assemblyPath);
    var shipped = ApiBaseline.Read(shippedPath);

    if (fold) {
        // The release ritual: what was approved becomes what was shipped, and the approval file
        // starts again empty. `Approved` is the same method the check uses to decide what is
        // allowed, so a fold cannot promise something the gate would have refused.
        var unshipped = ApiBaseline.Read(unshippedPath);
        var approved = ApiBaseline.Approved(shipped, unshipped);

        ApiBaseline.Write(shippedPath, [.. approved]);
        ApiBaseline.Write(unshippedPath, []);

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: folded {unshipped.Count} entries in, {approved.Count} shipped."
            )
        );

        continue;
    }

    if (update) {
        // Written even when it would be empty, and never rewritten from the surface. An absent
        // Shipped file and an empty one say different things — "this project is not covered" and
        // "this project has published nothing yet" — and only the second is true here.
        if (!File.Exists(shippedPath)) {
            ApiBaseline.Write(shippedPath, []);
        }

        var rebased = ApiBaseline.Rebase(surface, shipped);
        ApiBaseline.Write(unshippedPath, rebased);

        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{name}: {surface.Count} entries, {rebased.Count} unshipped.")
        );

        continue;
    }

    if (!File.Exists(shippedPath) && !File.Exists(unshippedPath)) {
        Console.Error.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: no API baseline at all — {surface.Count} public entries approved by nobody."
            )
        );

        differing++;

        continue;
    }

    var difference = ApiBaseline.Compare(surface, shipped, ApiBaseline.Read(unshippedPath));

    if (difference.IsEmpty) {
        continue;
    }

    differing++;

    Console.Error.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: {difference.Added.Count} unapproved addition(s), {difference.Removed.Count} removal(s)."
        )
    );

    Report('+', difference.Added);
    Report('-', difference.Removed);
}

if (fold) {
    Console.WriteLine("Everything approved is now shipped. From here a removal is a breaking change.");

    return ExitOk;
}

if (update) {
    Console.WriteLine(
        string.Create(CultureInfo.InvariantCulture, $"Rewrote the baselines of {pairs.Count} assemblies.")
    );

    Console.WriteLine("Read the diff before committing: an approval nobody looked at approves whatever was there.");

    return ExitOk;
}

if (differing > 0) {
    // The failure has to say what to do, or it becomes the gate somebody disables.
    Console.Error.WriteLine();
    Console.Error.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"trinix-api-check: {differing} of {pairs.Count} assemblies differ from their committed public API."
        )
    );
    Console.Error.WriteLine();
    Console.Error.WriteLine(
        "  + is an addition no PublicAPI.Unshipped.txt line approves. Adding the line is how somebody"
    );
    Console.Error.WriteLine("    approves it, and `public` is supposed to cost that much.");
    Console.Error.WriteLine("  - is API the baseline promises and the assembly no longer has. That breaks a caller,");
    Console.Error.WriteLine("    and it is the direction a gate that only watched additions would have let through.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Intended?  ./scripts/check-api.ps1 -Update    then read the diff.");

    return ExitDiffers;
}

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"OK: {pairs.Count} assemblies match the public API committed beside them."
    )
);

return ExitOk;

void Report(char sign, IReadOnlyList<string> entries) {
    foreach (var entry in entries.Take(MaxReported)) {
        // Truncated on the console, never in the file. Trinix.Services.Contracts' baseline
        // contains the generated D-Bus introspection XML as a `const string`, which is the
        // most useful line in it — a protocol change *is* a changed <method> element — and
        // is also fifteen hundred characters long. The console says which entry moved; the
        // diff of PublicAPI.Unshipped.txt says what moved in it.
        Console.Error.WriteLine(
            entry.Length <= MaxEntryWidth
                ? $"  {sign} {entry}"
                : $"  {sign} {entry[..MaxEntryWidth]}… (+{entry.Length - MaxEntryWidth} chars; see the baseline diff)"
        );
    }

    if (entries.Count > MaxReported) {
        Console.Error.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"  {sign} … and {entries.Count - MaxReported} more.")
        );
    }
}

void Usage() {
    Console.WriteLine(
        """
        trinix-api-check [--update | --fold] <project.csproj> <assembly.dll> [<project> <assembly> …]

          Compares the public surface of each assembly with the PublicAPI.Shipped.txt and
          PublicAPI.Unshipped.txt committed beside the project that produced it. Both
          directions: an addition nobody approved fails, and so does a removal.

          Arguments come in pairs because src/Directory.Build.props sets ArtifactsPath, so a
          built assembly is not under the project that produced it and the pairing cannot be
          worked out from the assembly's path.

          --update, -u   Rewrite PublicAPI.Unshipped.txt from what the assemblies contain
                         instead of failing. Shipped API is never rewritten; a shipped entry
                         that is gone becomes a *REMOVED* line.
          --fold, -f     The release: fold Unshipped into Shipped and empty it. Run at a tag,
                         never as part of a check.
          --help, -h     This text.

          Normally reached through ./scripts/check-api.ps1, which decides which assemblies
          are covered and builds them first.

        Exit codes: 0 the surfaces match, 1 they do not, 2 the arguments or the inputs are wrong.
        """
    );
}

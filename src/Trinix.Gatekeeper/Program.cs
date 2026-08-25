using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Trinix.Bundle;
using Trinix.Gatekeeper;

// open — verify an application bundle, then launch it.
//
// This is Trinix's Gatekeeper: the one place where "is this allowed to run" is
// asked. Everything about the answer is deliberate.
//
// It verifies on *every* launch, not only the first. Apple's Gatekeeper checks
// once and then relies on the kernel to enforce code signing page by page for
// the life of the process; Trinix has fs-verity, which does the same job, but
// only when the filesystem underneath supports it — an application running from
// a developer's build directory or a bind mount has no such protection. A cache
// would therefore be trusted in exactly the cases where it is least justified.
// A full verification of a bundle costs a SHA-256 pass over a few megabytes,
// which is a few milliseconds; that is a good trade for never having to reason
// about when the cached answer stopped being true.
//
// It fails closed and says why in one line. "Refused" with no reason turns
// every packaging mistake into a support conversation.

if (args.Length == 0 || args[0] is "--help" or "-h") {
    Console.WriteLine(
        """
        open — verify and launch a Trinix application

          open <Application.app | identifier> [arguments...]
          open --wait <Application.app> [arguments...]
          open --verify-only <Application.app>
          open --sandbox <Application.app> [arguments...]
          open --print-unit <Application.app>

        Options, which must come before the bundle:
          --wait          Replace this process with the application and share its
                          terminal, instead of launching it and returning.
          --verify-only   Check the bundle and report, but do not launch it.
          --sandbox       Launch inside a transient systemd unit built from the
                          bundle's signed permissions, instead of executing it
                          directly. Needs privilege, a composed root, and — for a
                          networked application — the shared namespace; it names
                          whichever of those is missing rather than failing
                          obscurely.
          --print-unit    Build that unit, print it, and stop. Reads this machine's
                          systemd feature string, so what it prints is what this
                          machine would be sent.
          --trust DIR     Trust store to check against.
                          Default: /usr/share/trinix/pki/roots

        Also installed as trinix-open, which is the name to use in a script:
        `open` is a word, and a script should not depend on whose PATH wins.
        """
    );
    return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
}

var verifyOnly = false;
var wait = false;
var sandbox = false;
var printUnit = false;
string? trustDirectory = null;
var index = 0;

while (index < args.Length && args[index].StartsWith("--", StringComparison.Ordinal)) {
    switch (args[index]) {
        case "--verify-only":
            verifyOnly = true;
            index++;
            break;

        case "--wait":
            wait = true;
            index++;
            break;

        case "--sandbox":
            sandbox = true;
            index++;
            break;

        // ⚠ Implies --sandbox rather than being orthogonal to it. "Print the unit"
        // is only a question about the sandboxed path, and a --print-unit that
        // silently did nothing without --sandbox would be a flag whose failure mode
        // is an empty screen.
        case "--print-unit":
            printUnit = true;
            sandbox = true;
            index++;
            break;

        case "--trust":
            if (index + 1 >= args.Length) {
                Console.Error.WriteLine("open: --trust needs a directory");
                return ExitCode.Usage;
            }

            trustDirectory = args[index + 1];
            index += 2;
            break;

        // Everything after this belongs to the application, so that an
        // application may have options of its own without colliding with these.
        case "--":
            index++;
            goto done;

        default:
            Console.Error.WriteLine($"open: unknown option {args[index]}");
            return ExitCode.Usage;
    }
}

done:

if (index >= args.Length) {
    Console.Error.WriteLine("open: no application given");
    return ExitCode.Usage;
}

// ⚠ Refused rather than reconciled. `--wait` means "be this application, on this
// terminal", which is what the unit files and the Phase 6 checks depend on; a
// transient unit's stdio belongs to the service manager unless systemd-run is
// asked for a pty, and quietly returning while the application ran elsewhere
// would change what --wait means for every existing caller. When there is a
// reason to want both, it is a decision about --pty and it should be made
// deliberately rather than inherited from this line.
if (sandbox && wait) {
    Console.Error.WriteLine("open: --wait and --sandbox cannot be combined");
    Console.Error.WriteLine(
        "  --wait makes the application this process; --sandbox makes it a unit of systemd's."
    );

    return ExitCode.Usage;
}

var requested = args[index];
var applicationArguments = args[(index + 1)..];

var bundlePath = Resolve(requested);
if (bundlePath is null) {
    Console.Error.WriteLine(
        $"open: no application '{requested}' — looked in {BundleInstaller.ApplicationsDirectory}"
    );
    return ExitCode.Failed;
}

TrustStore trust;
try {
    trust = TrustStore.Load(trustDirectory);
} catch (BundleException e) {
    // A missing trust store is a broken system, not a bad application, and
    // saying so is the difference between checking the image and blaming the
    // developer.
    Console.Error.WriteLine($"open: {e.Message}");
    return ExitCode.Failed;
}

VerificationResult result;
try {
    result = await BundleVerifier.VerifyAsync(bundlePath, trust).ConfigureAwait(false);
} catch (IOException e) {
    Console.Error.WriteLine($"open: {bundlePath}: {e.Message}");
    return ExitCode.Failed;
} catch (UnauthorizedAccessException e) {
    Console.Error.WriteLine($"open: {bundlePath}: {e.Message}");
    return ExitCode.Failed;
}

if (!result.Ok) {
    Console.Error.WriteLine($"open: refused to launch {Path.GetFileName(bundlePath)}");
    Console.Error.WriteLine($"  {Explain(result.Failure)}");
    Console.Error.WriteLine($"  {result.Message}");
    return ExitCode.Refused;
}

Console.WriteLine($"open: verified {result.Message}");

// Compatibility, checked after trust and reported differently: an application
// that needs a newer system is in the wrong place, not suspicious.
var systemVersion = SystemVersion.Current();
if (!SystemVersion.Satisfies(systemVersion, result.Info!.MinimumSystemVersion)) {
    Console.Error.WriteLine($"open: cannot launch {Path.GetFileName(bundlePath)}");
    Console.Error.WriteLine(
        $"  It needs Trinix {result.Info.MinimumSystemVersion} or newer; this system is {systemVersion}."
    );
    return ExitCode.Failed;
}

if (verifyOnly) {
    Console.WriteLine($"BUNDLE-VERIFIED {result.Info.Identifier} {result.Info.Version}");
    return ExitCode.Ok;
}

// ⚠ Doc 04's seam, now crossed — behind a flag, and the flag is the honest part.
// The question that blocked this was settled on 2026-08-25 in a booted VM, and
// the answer was neither of the two that were predicted: of the five properties
// systemd implements with libseccomp, two fail the D-Bus call and three are
// accepted while enforcing nothing at all. Trinix.Sandbox now models all three
// outcomes and records the third in SandboxUnit.Gaps, which Sandboxed.Launch
// puts in the journal at every launch.
//
// ⚠ What is still missing is not information either. It is three things nothing
// in this repository builds yet — the composed root, the shared network
// namespace, and a privileged path to the system manager — so a sandboxed launch
// refuses, by name, on every machine that exists today. Making it the default
// would be replacing a launcher that works with one that is right about a world
// that has not been built. SandboxPreflight is what turns that from an obscure
// systemd error into a sentence naming the directory and whose job it is.
if (sandbox) {
    return Sandboxed.Launch(bundlePath, result, applicationArguments, printUnit);
}

var entry = Path.Combine(bundlePath, result.Info.EntryPoint);

// The application learns where it lives from the environment rather than by
// inspecting its own argv, so that a bundle's resources are findable the same
// way from a managed process, a shell script or a native binary.
List<string> environment = [];
foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables()) {
    var name = (string)variable.Key;
    if (name is "TRINIX_BUNDLE" or "TRINIX_BUNDLE_IDENTIFIER" or "TRINIX_BUNDLE_RESOURCES") {
        continue;
    }

    environment.Add(string.Create(CultureInfo.InvariantCulture, $"{name}={variable.Value}"));
}

environment.Add($"TRINIX_BUNDLE={bundlePath}");
environment.Add($"TRINIX_BUNDLE_IDENTIFIER={result.Info.Identifier}");
environment.Add($"TRINIX_BUNDLE_RESOURCES={Path.Combine(bundlePath, BundleLayout.ResourcesDirectory)}");

// ⚠ Launching and returning is the default, because that is what `open` means:
// it is a request to the system to run something, not a way to run it here. A
// window that closed when the terminal it was typed into closed would be a
// window nobody could launch from a shell — which on a system whose shell is
// the point would be most of them.
//
// --wait is the other half, and the unit files use it: something that has to
// observe an application — a service manager, a check, a debugger — needs the
// application to *be* this process rather than a sibling of it.
if (!wait) {
    var spawnError = Launcher.Spawn(entry, applicationArguments, environment, out var pid);
    if (spawnError != 0) {
        Console.Error.WriteLine(
            $"open: could not launch {entry}: {new Win32Exception(spawnError).Message}"
        );

        return ExitCode.Failed;
    }

    Console.WriteLine($"BUNDLE-LAUNCHED {result.Info.Identifier} {result.Info.Version} pid {pid}");
    return ExitCode.Ok;
}

var errno = Launcher.Exec(entry, applicationArguments, environment);

// Only reached if the exec failed.
Console.Error.WriteLine($"open: could not execute {entry}: {new Win32Exception(errno).Message}");
return ExitCode.Failed;

// --- helpers ----------------------------------------------------------------

static string? Resolve(string requested) {
    // A path, given directly. The common case from a shell and from a Files
    // app, which both hand over something that exists.
    if (Directory.Exists(requested)) {
        return Path.GetFullPath(requested);
    }

    // A name or an identifier. /Applications first, because that is where an
    // installed application is; the receipts second, because an application
    // installed somewhere else still has one.
    var byName = Path.Combine(BundleInstaller.ApplicationsDirectory, requested);
    if (Directory.Exists(byName)) {
        return byName;
    }

    var byNameWithExtension = byName + BundleLayout.Extension;
    if (Directory.Exists(byNameWithExtension)) {
        return byNameWithExtension;
    }

    foreach (var receipt in BundleInstaller.Installed()) {
        if (string.Equals(receipt.Identifier, requested, StringComparison.Ordinal)
            && Directory.Exists(receipt.Path)) {
            return receipt.Path;
        }
    }

    return null;
}

// One line per failure, written for the person in front of the machine rather
// than for the person who wrote the verifier.
static string Explain(BundleFailure failure) =>
    failure switch {
        BundleFailure.NotABundle => "That is not an application bundle.",
        BundleFailure.MalformedInfo => "The application's description is damaged or invalid.",
        BundleFailure.NotSigned => "The application is not signed. Trinix will not run unsigned code.",
        BundleFailure.MalformedSignature => "The application's signature is damaged.",
        BundleFailure.NoTrustStore =>
            "This system has no list of trusted developers, which is a fault in the system image.",
        BundleFailure.UntrustedSigner => "The application was signed by a developer this system does not trust.",
        BundleFailure.BadSignature =>
            "The application's signature does not match. It has been altered since it was signed.",
        BundleFailure.ContentMismatch => "The application has been modified since it was signed.",
        BundleFailure.FileSetMismatch =>
            "Files have been added to or removed from the application since it was signed.",
        BundleFailure.UnsupportedEntry => "The application contains something Trinix will not run.",
        BundleFailure.MalformedImage => "The distribution image is damaged.",
        BundleFailure.NotPermitted => "That operation needs more privilege than this session has.",

        // ⚠ A version problem wearing a refusal's clothes, and the wording is the
        // whole point of the arm: nothing is wrong with this bundle. Its signature
        // was good and its developer did nothing careless — it asks for authority
        // this system has never heard of, which means it was built for a newer
        // Trinix. Saying "refused" without saying "newer" sends a person looking
        // for a compromise that did not happen.
        BundleFailure.UnknownPermission =>
            "This application needs a newer version of Trinix: it asks for a permission this system does not define.",

        _ => "The application was refused."
    };

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

const int ExitOk = 0;
const int ExitUsage = 1;
const int ExitRefused = 2;
const int ExitFailed = 3;

if (args.Length == 0 || args[0] is "--help" or "-h") {
    Console.WriteLine(
        """
        open — verify and launch a Trinix application

          open <Application.app | identifier> [arguments...]
          open --wait <Application.app> [arguments...]
          open --verify-only <Application.app>

        Options, which must come before the bundle:
          --wait          Replace this process with the application and share its
                          terminal, instead of launching it and returning.
          --verify-only   Check the bundle and report, but do not launch it.
          --trust DIR     Trust store to check against.
                          Default: /usr/share/trinix/pki/roots

        Also installed as trinix-open, which is the name to use in a script:
        `open` is a word, and a script should not depend on whose PATH wins.
        """
    );
    return args.Length == 0 ? ExitUsage : ExitOk;
}

var verifyOnly = false;
var wait = false;
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

        case "--trust":
            if (index + 1 >= args.Length) {
                Console.Error.WriteLine("open: --trust needs a directory");
                return ExitUsage;
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
            return ExitUsage;
    }
}

done:

if (index >= args.Length) {
    Console.Error.WriteLine("open: no application given");
    return ExitUsage;
}

var requested = args[index];
var applicationArguments = args[(index + 1)..];

var bundlePath = Resolve(requested);
if (bundlePath is null) {
    Console.Error.WriteLine(
        $"open: no application '{requested}' — looked in {BundleInstaller.ApplicationsDirectory}"
    );
    return ExitFailed;
}

TrustStore trust;
try {
    trust = TrustStore.Load(trustDirectory);
} catch (BundleException e) {
    // A missing trust store is a broken system, not a bad application, and
    // saying so is the difference between checking the image and blaming the
    // developer.
    Console.Error.WriteLine($"open: {e.Message}");
    return ExitFailed;
}

VerificationResult result;
try {
    result = await BundleVerifier.VerifyAsync(bundlePath, trust).ConfigureAwait(false);
} catch (IOException e) {
    Console.Error.WriteLine($"open: {bundlePath}: {e.Message}");
    return ExitFailed;
} catch (UnauthorizedAccessException e) {
    Console.Error.WriteLine($"open: {bundlePath}: {e.Message}");
    return ExitFailed;
}

if (!result.Ok) {
    Console.Error.WriteLine($"open: refused to launch {Path.GetFileName(bundlePath)}");
    Console.Error.WriteLine($"  {Explain(result.Failure)}");
    Console.Error.WriteLine($"  {result.Message}");
    return ExitRefused;
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
    return ExitFailed;
}

if (verifyOnly) {
    Console.WriteLine($"BUNDLE-VERIFIED {result.Info.Identifier} {result.Info.Version}");
    return ExitOk;
}

var entry = Path.Combine(bundlePath, result.Info.EntryPoint);

// ⚠ The seam for doc 04's sandbox, and deliberately not crossed here. Everything
// constructing a transient unit would need is already in hand: result.Info
// carries the signed permission array, and result.Manifest carries the signed
// file list that Trinix.Sandbox's BundleRuntimeDetection reads the runtime kind
// out of. What is missing is not information but an answer — whether systemd-run
// accepts the properties that library emits on a systemd built without
// libseccomp, or fails the call, which are opposite outcomes and are settled by
// one run in a booted VM rather than by reading. Until then `open` execs the
// entry point directly, exactly as it always has, and an application is no less
// contained than it was yesterday.

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

        return ExitFailed;
    }

    Console.WriteLine($"BUNDLE-LAUNCHED {result.Info.Identifier} {result.Info.Version} pid {pid}");
    return ExitOk;
}

var errno = Launcher.Exec(entry, applicationArguments, environment);

// Only reached if the exec failed.
Console.Error.WriteLine($"open: could not execute {entry}: {new Win32Exception(errno).Message}");
return ExitFailed;

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

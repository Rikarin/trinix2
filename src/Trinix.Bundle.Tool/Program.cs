using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Trinix.Bundle;
using Trinix.Bundle.Tool;
using Trinix.Conformance;

// trinix-bundle — create, sign, package, verify and install Trinix applications.
//
// The verbs split into two groups that never run on the same machine. `pki`,
// `seal` and `pack` run on the build host and need a private key; `verify`,
// `install`, `list` and `info` run on a Trinix system and need only the trust
// store the image ships. They share one executable because they share every
// concept, and because a packaging tool that cannot check its own output is a
// tool whose output nobody checks.

const int ExitOk = 0;
const int ExitUsage = 1;
const int ExitRejected = 2; // the thing was read, and refused
const int ExitFailed = 3; // the operation could not be attempted

if (args.Length == 0 || args[0] is "--help" or "-h" or "help") {
    Usage();
    return args.Length == 0 ? ExitUsage : ExitOk;
}

var verb = args[0];
var rest = args[1..];

try {
    return verb switch {
        "pki" => await Pki(rest).ConfigureAwait(false),
        "seal" => await Seal(rest).ConfigureAwait(false),
        "pack" => await Pack(rest).ConfigureAwait(false),
        "verify" => await Verify(rest).ConfigureAwait(false),
        "install" => await Install(rest).ConfigureAwait(false),
        "uninstall" => Uninstall(rest),
        "list" => List(),
        "info" => await Info(rest).ConfigureAwait(false),
        "doctor" => await Doctor(rest).ConfigureAwait(false),
        _ => Unknown(verb)
    };
} catch (UsageException e) {
    Console.Error.WriteLine($"trinix-bundle {verb}: {e.Message}");
    return ExitUsage;
} catch (BundleException e) {
    Console.Error.WriteLine($"trinix-bundle: {e.Message}");
    return e.Failure is BundleFailure.NotPermitted ? ExitFailed : ExitRejected;
} catch (IOException e) {
    Console.Error.WriteLine($"trinix-bundle: {e.Message}");
    return ExitFailed;
} catch (UnauthorizedAccessException e) {
    Console.Error.WriteLine($"trinix-bundle: {e.Message}");
    return ExitFailed;
}

int Unknown(string name) {
    Console.Error.WriteLine($"trinix-bundle: unknown command '{name}'");
    Usage();
    return ExitUsage;
}

void Usage() {
    Console.WriteLine(
        """
        trinix-bundle — Trinix application bundles, signatures and distribution images

        Building and signing (needs a private key):
          pki init      --certificate P --key P [--name N] [--organisation O] [--not-before D]
                        Create a development root certificate authority.
          pki identity  --root-certificate P --root-key P --certificate P --key P [--name N]
                        Issue a developer signing certificate from that root.
          pki show      <certificate.pem>
          seal          <bundle.app> --certificate P --key P [--architecture A] [--signed-at D]
                        Write Contents/_Signature into a bundle.
          pack          <bundle.app> --output P --certificate P --key P [--compress]
                        Build and sign a .tdi distribution image around a sealed bundle.

        Checking and installing (needs only the trust store):
          verify        <bundle.app | image.tdi> [--trust DIR]
          install       <image.tdi> [--destination DIR] [--trust DIR] [--no-fs-verity]
          uninstall     <bundle.app>
          list          Installed applications, from their receipts.
          info          <bundle.app | image.tdi>

        Conformance:
          doctor        <bundle.app> [--trust DIR] [--store] [--strict] [--width N]
                        Everything wrong with a bundle, and what to do about it.
                        --store  doc 09 § Submission's subset: the repository's gates
                                 are reported as errors rather than warnings.
                        --strict warnings fail too. For CI, not for a person.

        --trust defaults to /usr/share/trinix/pki/roots, which the system image carries.
        """
    );
}

// --- pki --------------------------------------------------------------------

async Task<int> Pki(string[] arguments) {
    await Task.CompletedTask.ConfigureAwait(false);

    if (arguments.Length == 0) {
        throw new UsageException("expected init, identity or show");
    }

    switch (arguments[0]) {
        case "init": {
            var line = CommandLine.Parse(
                arguments[1..],
                new HashSet<string>(StringComparer.Ordinal) {
                    "certificate",
                    "key",
                    "name",
                    "organisation",
                    "not-before"
                },
                new HashSet<string>(StringComparer.Ordinal)
            );

            var notBefore = ParseTime(line.Value("not-before")) ?? DateTimeOffset.UtcNow.AddDays(-1);
            using var root = DeveloperPki.CreateRoot(
                line.Value("name") ?? "Trinix Development Root",
                line.Value("organisation") ?? "Trinix",
                notBefore
            );

            DeveloperPki.Save(root, line.Required("certificate"), line.Required("key"));
            Console.WriteLine($"trinix-bundle: created {root.Subject}");
            Console.WriteLine($"  valid       {root.NotBefore:yyyy-MM-dd} .. {root.NotAfter:yyyy-MM-dd}");
            Console.WriteLine($"  certificate {line.Required("certificate")}");
            Console.WriteLine($"  private key {line.Required("key")}");
            return ExitOk;
        }

        case "identity": {
            var line = CommandLine.Parse(
                arguments[1..],
                new HashSet<string>(StringComparer.Ordinal) {
                    "root-certificate",
                    "root-key",
                    "certificate",
                    "key",
                    "name",
                    "organisation",
                    "not-before"
                },
                new HashSet<string>(StringComparer.Ordinal)
            );

            using var root = DeveloperPki.Load(line.Required("root-certificate"), line.Required("root-key"));
            var notBefore = ParseTime(line.Value("not-before")) ?? DateTimeOffset.UtcNow.AddDays(-1);

            using var identity = DeveloperPki.CreateIdentity(
                root,
                line.Value("name") ?? "Trinix Developer",
                line.Value("organisation") ?? "Trinix",
                notBefore
            );

            DeveloperPki.Save(identity, line.Required("certificate"), line.Required("key"));
            Console.WriteLine($"trinix-bundle: issued {identity.Subject}");
            Console.WriteLine($"  issued by   {identity.Issuer}");
            Console.WriteLine($"  valid       {identity.NotBefore:yyyy-MM-dd} .. {identity.NotAfter:yyyy-MM-dd}");
            return ExitOk;
        }

        case "show": {
            var line = CommandLine.Parse(
                arguments[1..],
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)
            );

            using var certificate = X509Certificate2.CreateFromPem(
                await File.ReadAllTextAsync(line.PositionalAt(0, "a certificate")).ConfigureAwait(false)
            );
            Console.WriteLine($"subject      {certificate.Subject}");
            Console.WriteLine($"issuer       {certificate.Issuer}");
            Console.WriteLine($"serial       {certificate.SerialNumber}");
            Console.WriteLine($"valid        {certificate.NotBefore:u} .. {certificate.NotAfter:u}");
            Console.WriteLine(
                $"sha256       {Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256))}"
            );
            foreach (var extension in certificate.Extensions) {
                Console.WriteLine(
                    $"extension    {extension.Oid?.FriendlyName ?? extension.Oid?.Value}{(extension.Critical ? " (critical)" : "")}"
                );
            }

            return ExitOk;
        }

        default:
            throw new UsageException($"unknown pki command '{arguments[0]}'");
    }
}

// --- seal -------------------------------------------------------------------

async Task<int> Seal(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal) { "certificate", "key", "architecture", "signed-at" },
        new HashSet<string>(StringComparer.Ordinal)
    );

    var bundlePath = line.PositionalAt(0, "a bundle directory");
    using var identity = DeveloperPki.Load(line.Required("certificate"), line.Required("key"));

    var manifest = await BundleSealer.SealAsync(
            bundlePath,
            identity,
            line.Value("architecture") ?? "any",
            ParseTime(line.Value("signed-at")) ?? DateTimeOffset.UtcNow
        )
        .ConfigureAwait(false);

    Console.WriteLine($"trinix-bundle: sealed {Path.GetFileName(Path.TrimEndingDirectorySeparator(bundlePath))}");
    Console.WriteLine($"  identity    {manifest.Identifier} {manifest.Version} ({manifest.Architecture})");
    Console.WriteLine($"  files       {manifest.Entries.Count}");
    Console.WriteLine($"  merkle root {manifest.MerkleRoot}");
    Console.WriteLine($"  signed by   {identity.Subject}");
    return ExitOk;
}

// --- pack -------------------------------------------------------------------

async Task<int> Pack(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal) { "output", "certificate", "key", "build-time" },
        new HashSet<string>(StringComparer.Ordinal) { "compress" }
    );

    var bundlePath = line.PositionalAt(0, "a bundle directory");
    var output = line.Required("output");
    using var identity = DeveloperPki.Load(line.Required("certificate"), line.Required("key"));

    // Packing an unsealed bundle would produce a distribution image that is
    // signed and contains something that is not, which is worse than either.
    if (!File.Exists(Path.Combine(bundlePath, BundleLayout.ManifestPath))) {
        throw new BundleException(
            BundleFailure.NotSigned,
            $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(bundlePath))} is not sealed — run `trinix-bundle seal` first"
        );
    }

    var footer = await DistributionImage.PackAsync(
            bundlePath,
            output,
            identity,
            ParseTime(line.Value("build-time")) ?? DateTimeOffset.UnixEpoch,
            line.Has("compress")
        )
        .ConfigureAwait(false);

    Console.WriteLine($"trinix-bundle: packed {output}");
    Console.WriteLine($"  payload     {footer.PayloadLength} bytes of erofs");
    Console.WriteLine($"  sha256      {Convert.ToHexStringLower(footer.PayloadSha256)}");
    Console.WriteLine($"  signed by   {identity.Subject}");
    return ExitOk;
}

// --- verify -----------------------------------------------------------------

async Task<int> Verify(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal) { "trust" },
        new HashSet<string>(StringComparer.Ordinal)
    );

    var target = line.PositionalAt(0, "a bundle or a distribution image");
    var trust = TrustStore.Load(line.Value("trust"));

    var result = target.EndsWith(BundleLayout.ImageExtension, StringComparison.Ordinal)
        ? await DistributionImage.VerifyAsync(target, trust).ConfigureAwait(false)
        : await BundleVerifier.VerifyAsync(target, trust).ConfigureAwait(false);

    if (result.Ok) {
        // A single grep-able line, because this is what the VM gate and any
        // future CI step actually assert on.
        Console.WriteLine($"BUNDLE-OK {result.Message}");
        if (result.AnchorSubject is not null) {
            Console.WriteLine($"  anchored at {result.AnchorSubject}");
        }

        if (result.Manifest is not null) {
            Console.WriteLine($"  merkle root {result.Manifest.MerkleRoot}");
        }

        return ExitOk;
    }

    Console.Error.WriteLine($"BUNDLE-REJECTED {result.Failure} {result.Message}");
    return ExitRejected;
}

// --- install ----------------------------------------------------------------

async Task<int> Install(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal) { "destination", "trust" },
        new HashSet<string>(StringComparer.Ordinal) { "no-fs-verity" }
    );

    var image = line.PositionalAt(0, "a .tdi distribution image");
    var trust = TrustStore.Load(line.Value("trust"));

    var result = await BundleInstaller.InstallAsync(image, trust, line.Value("destination"), !line.Has("no-fs-verity"))
        .ConfigureAwait(false);

    Console.WriteLine(
        $"BUNDLE-INSTALLED {result.Receipt.Identifier} {result.Receipt.Version} -> {result.Receipt.Path}"
    );
    Console.WriteLine($"  signed by   {result.Receipt.SignerSubject}");
    Console.WriteLine($"  merkle root {result.Receipt.MerkleRoot}");
    if (result.Receipt.FsVerity is { } verity) {
        var detail = verity.Skipped == 0
            ? $"{verity.Enabled} file(s) sealed by the kernel"
            : $"{verity.Enabled} sealed, {verity.Skipped} not ({verity.Reason})";
        Console.WriteLine($"  fs-verity   {detail}");
    }

    return ExitOk;
}

int Uninstall(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal)
    );

    var bundlePath = line.PositionalAt(0, "an installed bundle");
    BundleInstaller.Uninstall(bundlePath);
    Console.WriteLine($"trinix-bundle: removed {bundlePath}");
    return ExitOk;
}

int List() {
    var count = 0;
    foreach (var receipt in BundleInstaller.Installed()) {
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{receipt.Identifier,-32} {receipt.Version,-10} {receipt.Path}"
            )
        );
        count++;
    }

    if (count == 0) {
        Console.WriteLine("no applications installed");
    }

    return ExitOk;
}

// --- info -------------------------------------------------------------------

async Task<int> Info(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal)
    );

    var target = line.PositionalAt(0, "a bundle or a distribution image");

    if (target.EndsWith(BundleLayout.ImageExtension, StringComparison.Ordinal)) {
        var footer = DistributionImage.ReadFooter(target);
        Console.WriteLine($"distribution image  {Path.GetFileName(target)}");
        Console.WriteLine($"  payload           {footer.PayloadLength} bytes");
        Console.WriteLine($"  payload sha256    {Convert.ToHexStringLower(footer.PayloadSha256)}");
        Console.WriteLine($"  signature         {footer.SignatureLength} bytes at {footer.SignatureOffset}");
        Console.WriteLine($"  certificates      {footer.CertificatesLength} bytes at {footer.CertificatesOffset}");
        return ExitOk;
    }

    var info = await BundleSealer.ReadInfoAsync(target).ConfigureAwait(false);
    Console.WriteLine($"application         {info.Name} {info.ShortVersion ?? info.Version}");
    Console.WriteLine($"  identifier        {info.Identifier}");
    Console.WriteLine($"  version           {info.Version}");
    Console.WriteLine($"  entry point       {info.EntryPoint}");
    Console.WriteLine($"  minimum system    {info.MinimumSystemVersion ?? "unspecified"}");
    Console.WriteLine(
        $"  permissions       {(info.Permissions.Count == 0 ? "none declared" : string.Join(", ", info.Permissions))}"
    );

    var manifestPath = Path.Combine(target, BundleLayout.ManifestPath);
    Console.WriteLine($"  signature         {(File.Exists(manifestPath) ? "present" : "absent")}");

    var problems = info.Validate();
    foreach (var problem in problems) {
        Console.WriteLine($"  problem           {problem}");
    }

    return problems.Count == 0 ? ExitOk : ExitRejected;
}

// --- doctor -----------------------------------------------------------------

async Task<int> Doctor(string[] arguments) {
    var line = CommandLine.Parse(
        arguments,
        new HashSet<string>(StringComparer.Ordinal) { "trust", "width" },
        new HashSet<string>(StringComparer.Ordinal) { "store", "strict" }
    );

    var report = await Trinix.Conformance.Doctor
        .RunAsync(
            line.PositionalAt(0, "a bundle directory"),
            new DoctorOptions { TrustDirectory = line.Value("trust"), Store = line.Has("store") }
        )
        .ConfigureAwait(false);

    DoctorWriter.Write(Console.Out, report, Width(line.Value("width")));

    // ⚠ Errors fail, warnings do not, and --strict is how CI asks for the stricter
    // rule rather than the tool asking everyone for it. A conformance tool whose
    // warnings fail the build is one whose warnings get deleted; doc 16 runs this
    // over Trinix's own applications, which is the caller that should be strict.
    if (!report.Ok) {
        return ExitRejected;
    }

    return line.Has("strict") && report.Count(Severity.Warning) > 0 ? ExitRejected : ExitOk;
}

// A UsageException rather than letting int.Parse throw: `--width wide` is somebody
// writing the command wrong, and it should read as that rather than as a crash in
// the tool that was about to tell them what is wrong with their bundle.
static int Width(string? value) {
    if (value is null) {
        return DoctorWriter.DefaultWidth;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width < 48) {
        throw new UsageException($"--width takes a number of columns, at least 48 (got '{value}')");
    }

    return width;
}

// ISO 8601, or @<unix seconds> — the second form because every caller here is a
// build script, and a build script's notion of "now" is SOURCE_DATE_EPOCH.
static DateTimeOffset? ParseTime(string? value) {
    if (value is null) {
        return null;
    }

    if (value.StartsWith('@')) {
        return DateTimeOffset.FromUnixTimeSeconds(long.Parse(value[1..], CultureInfo.InvariantCulture));
    }

    return DateTimeOffset.Parse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
    );
}

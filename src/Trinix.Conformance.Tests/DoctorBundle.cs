using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Trinix.Bundle;

namespace Trinix.Conformance.Tests;

/// <summary>
///     A real bundle on disk, broken on purpose.
/// </summary>
/// <remarks>
///     <para>
///         Everything here is a real file, for the reason <c>Trinix.Bundle.Tests</c>
///         gives: half of what doctor checks is mode bits, link targets and directory
///         entries, and an abstraction over those would be a second implementation of
///         the thing under test. The PKI is generated per bundle — P-256 keygen is
///         microseconds — so no test can be broken by another's leftovers.
///     </para>
///     <para>
///         ⚠ <c>Info.json</c> is written as text rather than through
///         <see cref="BundleInfo" />. Several of the cases are documents the typed
///         model cannot express — a schema marker from the future, a
///         <c>usageDescriptions</c> key that is not a permission, a file that is not
///         JSON at all — and a fixture that could only produce valid documents would
///         quietly skip exactly the checks that exist for invalid ones.
///     </para>
/// </remarks>
sealed class DoctorBundle : IDisposable {
    DoctorBundle(string scratch, string path, X509Certificate2 root, X509Certificate2 identity) {
        Scratch = scratch;
        Path = path;
        Root = root;
        Identity = identity;
    }

    internal string Scratch { get; }

    internal string Path { get; }

    internal X509Certificate2 Root { get; }

    internal X509Certificate2 Identity { get; }

    /// <summary>A trust store holding exactly this bundle's root.</summary>
    internal TrustStore Trust => TrustStore.FromCertificates([Root], "test");

    /// <summary>Where that store lives on disk, for the checks that load one by path.</summary>
    internal string TrustDirectory {
        get {
            var directory = System.IO.Path.Combine(Scratch, "roots");
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "root.pem"), Root.ExportCertificatePem());
            return directory;
        }
    }

    public void Dispose() {
        Identity.Dispose();
        Root.Dispose();

        try {
            if (Directory.Exists(Scratch)) {
                Directory.Delete(Scratch, true);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Cleanup must never be the reason a test fails.
        }
    }

    /// <summary>
    ///     A bundle that passes everything doctor can fail it for.
    /// </summary>
    /// <param name="label">Names the scratch directory, so a leftover is traceable.</param>
    /// <param name="info">The <c>Info.json</c> to write, or the good one.</param>
    /// <param name="bundleName">The directory name, including its extension.</param>
    /// <param name="notBefore">Validity start for the generated certificates.</param>
    internal static DoctorBundle Create(
        string label,
        string? info = null,
        string bundleName = "Hello.app",
        DateTimeOffset? notBefore = null
    ) {
        var scratch = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"trinix-doctor-{label}-{Guid.NewGuid():n}")
        );
        Directory.CreateDirectory(scratch);

        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var root = DeveloperPki.CreateRoot("Trinix Doctor Test Root", "Trinix", start);
        var identity = DeveloperPki.CreateIdentity(root, "Trinix Doctor Test", "Trinix", start);

        var path = System.IO.Path.Combine(scratch, bundleName);
        Directory.CreateDirectory(System.IO.Path.Combine(path, BundleLayout.BinDirectory));

        var bundle = new DoctorBundle(scratch, path, root, identity);
        bundle.Write(BundleLayout.InfoPath, info ?? GoodInfo());
        bundle.Write(BundleLayout.BinDirectory + "/hello", "#!/bin/sh\necho hello\n", executable: true);
        return bundle;
    }

    /// <summary>
    ///     An <c>Info.json</c> with no findings in it, and the pieces a test wants to
    ///     spoil one at a time.
    /// </summary>
    internal static string GoodInfo(
        string schema = BundleSchema.Info,
        string identifier = "io.trinix.hello",
        string name = "Hello",
        string version = "1.0.0",
        string? shortVersion = "1.0",
        string entryPoint = "Contents/Bin/hello",
        string? minimumSystemVersion = "0.3",
        string permissions = "[]",
        string? usageDescriptions = null
    ) {
        var text = new StringBuilder("{\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"schema\": \"{schema}\",\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"identifier\": \"{identifier}\",\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"name\": \"{name}\",\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"version\": \"{version}\",\n");

        if (shortVersion is not null) {
            text.Append(CultureInfo.InvariantCulture, $"  \"shortVersion\": \"{shortVersion}\",\n");
        }

        if (minimumSystemVersion is not null) {
            text.Append(CultureInfo.InvariantCulture, $"  \"minimumSystemVersion\": \"{minimumSystemVersion}\",\n");
        }

        if (usageDescriptions is not null) {
            text.Append(CultureInfo.InvariantCulture, $"  \"usageDescriptions\": {usageDescriptions},\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"  \"permissions\": {permissions},\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"entryPoint\": \"{entryPoint}\"\n");
        return text.Append("}\n").ToString();
    }

    /// <summary>Write a file into the bundle, creating any directories it needs.</summary>
    internal void Write(string relativePath, string content, bool executable = false) {
        var full = At(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        // ⚠ No byte-order mark. Encoding.UTF8 writes one, and an Info.json with a BOM
        // is a file System.Text.Json refuses to parse — which doctor reports, and which
        // a fixture must therefore be able to produce on purpose rather than by
        // accident. See ABomInInfoJsonIsExplainedRatherThanQuoted.
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (OperatingSystem.IsWindows()) {
            return;
        }

        File.SetUnixFileMode(
            full,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            | (executable ? UnixFileMode.UserExecute : 0)
        );
    }

    /// <summary>Absolute path of something inside the bundle.</summary>
    internal string At(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Make a directory inside the bundle and leave it empty.</summary>
    internal void MakeDirectory(string relativePath) => Directory.CreateDirectory(At(relativePath));

    /// <summary>Add a symbolic link inside the bundle.</summary>
    internal void Link(string relativePath, string target) {
        var full = At(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.CreateSymbolicLink(full, target);
    }

    /// <summary>Set a file's mode bits.</summary>
    internal void Chmod(string relativePath, UnixFileMode mode) {
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(At(relativePath), mode);
        }
    }

    /// <summary>Seal the bundle with <see cref="Identity" />.</summary>
    internal Task<BundleManifest> SealAsync(DateTimeOffset? signedAt = null) =>
        BundleSealer.SealAsync(Path, Identity, "any", signedAt ?? DateTimeOffset.UtcNow);

    /// <summary>Run doctor over it.</summary>
    internal Task<DoctorReport> RunAsync(DoctorOptions? options = null) =>
        Doctor.RunAsync(Path, options ?? new DoctorOptions());
}

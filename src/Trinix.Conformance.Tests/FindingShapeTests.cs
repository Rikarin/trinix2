using Trinix.Bundle;
using Trinix.Sandbox;

namespace Trinix.Conformance.Tests;

/// <summary>
///     Properties of the output, asserted mechanically rather than noticed in review.
/// </summary>
/// <remarks>
///     Doc 01's whole argument for <c>doctor</c> is that a conformance tool whose
///     message does not say what to do is one people run once. That is a claim about
///     every finding the tool can produce, which makes it a claim worth checking with
///     a loop rather than with a reviewer's attention — the finding that gets added
///     without a fix will be added on a Friday, in the check nobody re-reads.
/// </remarks>
public sealed class FindingShapeTests {
    /// <summary>The five parts of a bundle every check id begins with.</summary>
    static readonly string[] Sections = ["info", "permissions", "contents", "signature", "sandbox"];

    /// <summary>
    ///     A bundle broken in as many ways as one bundle can be at once.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a substitute for <c>DoctorTests</c>, which fires each check in
    ///     isolation so that its polarity is known. This one exists so the assertions
    ///     below have a wide sample, and so that "one bundle, thirty findings, still
    ///     readable" is something somebody has actually looked at.
    /// </remarks>
    internal static DoctorBundle Sink(string label = "sink") {
        var bundle = DoctorBundle.Create(
            label,
            DoctorBundle.GoodInfo(
                identifier: "io.Trinix_Broken",
                name: "Broken",
                version: "1.0.0-beta",
                shortVersion: " ",
                entryPoint: "Contents/Bin/absent",
                minimumSystemVersion: "0.3-rc1",
                permissions: "[\"display\", \"display\", \"files.home\", \"system.input\", \"camera\"]",
                usageDescriptions: "{ \"devices.usb\": \"To talk to the scanner.\" }"
            ),
            bundleName: "Broken.app"
        );

        bundle.Write("README.md", "read me\n");
        bundle.Write(BundleLayout.ResourcesDirectory + "/.DS_Store", "junk\n");
        bundle.Write(BundleLayout.BinDirectory + "/hello.pdb", "symbols\n");
        bundle.Write(BundleLayout.ResourcesDirectory + "/tool.sh", "#!/bin/sh\n", executable: true);
        bundle.Write(BundleLayout.ResourcesDirectory + "/notes.txt", "hello\n");
        bundle.Chmod(
            BundleLayout.ResourcesDirectory + "/notes.txt",
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
        );

        bundle.MakeDirectory(BundleLayout.FrameworksDirectory);
        bundle.Link(BundleLayout.ResourcesDirectory + "/passwd", "/etc/passwd");
        return bundle;
    }

    internal static Task<DoctorReport> SinkReportAsync(DoctorBundle bundle, bool store = false) =>
        bundle.RunAsync(new DoctorOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            SystemVersion = "0.3",
            Store = store
        });

    /// <summary>⚠ The assertion doc 01's argument reduces to.</summary>
    [Fact]
    public async Task EveryWarningAndEveryErrorSaysWhatToDo() {
        using var bundle = Sink();
        var report = await SinkReportAsync(bundle);

        Assert.NotEmpty(report.Findings);

        foreach (var finding in report.Findings) {
            Assert.False(string.IsNullOrWhiteSpace(finding.What), finding.Check);
            Assert.False(string.IsNullOrWhiteSpace(finding.Why), finding.Check);

            if (finding.Severity != Severity.Note) {
                Assert.False(string.IsNullOrWhiteSpace(finding.Fix), finding.Check + " has no fix");
            }
        }
    }

    /// <summary>
    ///     A check id is an identity somebody will grep for and waive by name, so it
    ///     has to look like one — and its first component is the part of the bundle it
    ///     is about, which is what lets a caller group findings without a second field.
    /// </summary>
    [Fact]
    public async Task EveryCheckIdIsLowercaseAndDottedAndNamesItsSection() {
        using var bundle = Sink("ids");
        var report = await SinkReportAsync(bundle);

        foreach (var check in report.ChecksRun) {
            Assert.Equal(check.ToLowerInvariant(), check);
            Assert.Contains(".", check, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", check, StringComparison.Ordinal);
        }

        foreach (var finding in report.Findings) {
            Assert.Contains(finding.Section, Sections);
            Assert.StartsWith(finding.Section + ".", finding.Check, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     ⚠ The severity distinction has to be load-bearing, so an error has to mean
    ///     the one thing it promises: something refuses this bundle.
    /// </summary>
    [Fact]
    public async Task AnErrorMeansTheVerifierOrTheSealerRefusesTheBundle() {
        using var bundle = Sink("refused");
        var report = await SinkReportAsync(bundle);

        Assert.False(report.Ok);

        // The sealer is the first thing that would see this bundle, and it refuses it.
        var refusal = await Assert.ThrowsAsync<BundleException>(() => bundle.SealAsync());
        Assert.NotEqual(BundleFailure.None, refusal.Failure);
    }

    /// <summary>Warnings never fail a run; that is what makes the distinction usable.</summary>
    [Fact]
    public async Task AReportWithOnlyWarningsPasses() {
        using var bundle = DoctorBundle.Create("warnings-only", DoctorBundle.GoodInfo(name: "Greeting"));
        await bundle.SealAsync();

        var report = await bundle.RunAsync(new DoctorOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            SystemVersion = "0.3",
            TrustDirectory = bundle.TrustDirectory
        });

        Assert.True(report.Count(Severity.Warning) > 0, DoctorWriter.ToText(report));
        Assert.True(report.Ok);
    }

    /// <summary>
    ///     <c>--store</c> escalates doc 09's subset and nothing else.
    /// </summary>
    [Fact]
    public async Task StoreModeEscalatesOnlyTheSubmissionSubset() {
        using var bundle = DoctorBundle.Create("store", DoctorBundle.GoodInfo(
            name: "Greeting",
            permissions: "[\"files.home\"]"
        ));

        await bundle.SealAsync();
        var trust = bundle.TrustDirectory;

        DoctorOptions For(bool store) => new() {
            Capabilities = SandboxCapabilities.TrinixToday,
            SystemVersion = "0.3",
            TrustDirectory = trust,
            Store = store
        };

        var relaxed = await bundle.RunAsync(For(false));
        var strict = await bundle.RunAsync(For(true));

        // The same findings, read differently — not a different set of checks.
        Assert.Equal(relaxed.Findings.Count, strict.Findings.Count);

        Assert.True(relaxed.Ok);
        Assert.False(strict.Ok);

        // The name/directory mismatch is a warning under both: doc 09 does not gate it.
        Assert.Equal(
            Severity.Warning,
            strict.Findings.Single(f => f.Check == "info.name.matches-directory").SeverityFor(true)
        );
    }

    /// <summary>
    ///     The set of checks, pinned.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the test that fails when somebody adds a check and no test for it.
    ///     It cannot prove a check has been watched failing — only <c>DoctorTests</c>
    ///     does that, one case at a time — but it does make the inventory something a
    ///     person has to look at and add a line to, which is the moment to ask whether
    ///     the new check has a case of its own.
    /// </remarks>
    [Fact]
    public async Task TheCheckInventoryIsWhatIsWrittenDownHere() {
        string[] expected = [
            "contents.empty-directory",
            "contents.entry-point.executable",
            "contents.entry-point.location",
            "contents.entry-point.present",
            "contents.extension",
            "contents.icon",
            "contents.readable",
            "contents.resources.executable",
            "contents.signature-material",
            "contents.stray",
            "contents.symlink",
            "contents.top-level",
            "contents.writable",
            "info.identifier.characters",
            "info.identifier.form",
            "info.identifier.labels",
            "info.identifier.length",
            "info.minimum.declared",
            "info.minimum.form",
            "info.minimum.system",
            "info.name.matches-directory",
            "info.name.present",
            "info.readable",
            "info.schema",
            "info.version.form",
            "info.version.short",
            "permissions.display",
            "permissions.duplicate",
            "permissions.known",
            "permissions.store-reserved",
            "permissions.usage.present",
            "permissions.usage.undeclared",
            "sandbox.enforced",
            "sandbox.gaps",
            "sandbox.inert",
            "sandbox.network",
            "sandbox.runtime",
            "sandbox.systemd",
            "sandbox.unit",
            "signature.certificate",
            "signature.code-signing-eku",
            "signature.sealed",
            "signature.verify"
        ];

        using var sink = Sink("inventory");
        using var good = DoctorBundle.Create("inventory-good");
        await good.SealAsync();

        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var report in new[] {
            await SinkReportAsync(sink),
            await good.RunAsync(new DoctorOptions {
                Capabilities = SandboxCapabilities.TrinixToday,
                SystemVersion = "0.3",
                TrustDirectory = good.TrustDirectory
            })
        }) {
            seen.UnionWith(report.ChecksRun);
            seen.UnionWith(report.Skipped.Select(skipped => skipped.Check));
        }

        Assert.Equal(expected, seen);
    }

    /// <summary>
    ///     ⚠ <c>InfoChecks.Truncates</c> mirrors a private parse in
    ///     <see cref="SystemVersion" />, and the two agreeing is the only reason the R13
    ///     warning is true.
    /// </summary>
    /// <remarks>
    ///     Checked through the public behaviour: if a version reads as
    ///     <c>read</c>, then <c>read</c> must satisfy the original's minimum and vice
    ///     versa — which is what "the comparison sees this instead" means. When R13 is
    ///     decided and <c>Satisfies</c> changes, this fails, and the warning gets
    ///     rewritten rather than quietly becoming a lie.
    /// </remarks>
    [Theory]
    [InlineData("0.3", "0.3")]
    [InlineData("0.3-rc1", "0")]
    [InlineData("0.3.1-rc1", "0.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("rc1", "")]
    public async Task WhatDoctorSaysAVersionIsComparedAsIsWhatTheComparisonDoes(string value, string read) {
        // The claim: the system behaves as if it were `read`, for every minimum.
        foreach (var minimum in new[] { "0", "0.3", "0.3.1", "1.0" }) {
            Assert.Equal(
                SystemVersion.Satisfies(read, minimum),
                SystemVersion.Satisfies(value, minimum)
            );
        }

        // And doctor says so only when there is something to say.
        using var bundle = DoctorBundle.Create(
            "truncates",
            DoctorBundle.GoodInfo(minimumSystemVersion: value)
        );

        var report = await bundle.RunAsync(new DoctorOptions {
            Capabilities = SandboxCapabilities.TrinixToday, SystemVersion = "9.9"
        });

        var findings = report.Findings.Where(finding => finding.Check == "info.minimum.form").ToList();
        if (value == read) {
            Assert.Empty(findings);
            return;
        }

        var what = Assert.Single(findings).What;
        Assert.Contains(
            read.Length == 0 ? "no numeric component at all" : $"compared as '{read}'",
            what,
            StringComparison.Ordinal
        );
    }
}

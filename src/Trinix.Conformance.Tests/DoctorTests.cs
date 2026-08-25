using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Trinix.Bundle;
using Trinix.Sandbox;

namespace Trinix.Conformance.Tests;

/// <summary>
///     One test per check, on a bundle broken in exactly the way that check is about.
/// </summary>
/// <remarks>
///     ⚠ Each case asserts the severity as well as the check id, because the severity
///     is the load-bearing part of the design: <see cref="Severity.Error" /> promises
///     that something refuses this bundle, and a check that quietly became a warning
///     would keep passing a test that only looked for the id.
/// </remarks>
public sealed class DoctorTests {
    /// <summary>The capability set every sandbox assertion is made against.</summary>
    /// <remarks>
    ///     Pinned rather than probed. The default is to ask the machine, which answers
    ///     differently on a Mac and on a Linux CI runner with a real systemd — and the
    ///     interesting assertion here is about the image Trinix ships, not about
    ///     whatever is running the tests.
    /// </remarks>
    static readonly SandboxCapabilities Image = SandboxCapabilities.TrinixToday;

    /// <summary>
    ///     The options every case runs with.
    /// </summary>
    /// <remarks>
    ///     ⚠ <paramref name="system" /> defaults to a version rather than to
    ///     <see langword="null" />, so that the system-version checks answer the same
    ///     way on a Mac with no <c>/usr/lib/os-release</c> and on a Linux runner that
    ///     has one. A test whose result depends on the host's distribution is a test
    ///     that fails once, somewhere else, for a reason nobody can reproduce.
    /// </remarks>
    static DoctorOptions Options(string? trust = null, bool store = false, string? system = "0.3") =>
        new() { TrustDirectory = trust, Store = store, SystemVersion = system, Capabilities = Image };

    static Finding Single(DoctorReport report, string check) =>
        Assert.Single(report.Findings, finding => finding.Check == check);

    static void Silent(DoctorReport report, string check) =>
        Assert.DoesNotContain(
            report.Findings,
            finding => finding.Check == check && finding.Severity != Severity.Note
        );

    // --- the baseline ---------------------------------------------------------

    /// <summary>
    ///     ⚠ The most important test in the file. Every other one proves a check can
    ///     fire; this one proves it does not fire on a bundle that is fine, which is
    ///     the half that decides whether anybody keeps running the tool.
    /// </summary>
    [Fact]
    public async Task AGoodBundleHasNoErrorsAndNoWarnings() {
        using var bundle = DoctorBundle.Create("good", DoctorBundle.GoodInfo(permissions: "[\"display\"]"));
        await bundle.SealAsync();

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        Assert.True(
            report.Ok && report.Count(Severity.Warning) == 0,
            DoctorWriter.ToText(report)
        );
    }

    /// <summary>A run reports how many questions it asked, not only how many failed.</summary>
    [Fact]
    public async Task AGoodBundleStillRunsEveryCheck() {
        using var bundle = DoctorBundle.Create("counted");
        await bundle.SealAsync();

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        Assert.True(report.ChecksRun.Count > 30, string.Join(", ", report.ChecksRun));
        Assert.Distinct(report.ChecksRun);
    }

    // --- info -----------------------------------------------------------------

    [Fact]
    public async Task ADirectoryWithNoInfoJsonIsNotABundle() {
        using var bundle = DoctorBundle.Create("no-info");
        File.Delete(bundle.At(BundleLayout.InfoPath));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Error, Single(report, "info.readable").Severity);

        // Nothing below Info.json can say anything useful, so nothing tries.
        Assert.Single(report.Findings);
    }

    [Fact]
    public async Task InfoJsonThatIsNotJsonIsReportedAsMalformed() {
        using var bundle = DoctorBundle.Create("bad-json", "{ this is not json");

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.readable");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("Info.json", finding.What, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The half-hour nobody should spend twice: a file that looks perfect in
    ///     every editor, refused at offset zero.
    /// </summary>
    [Fact]
    public async Task ABomInInfoJsonIsExplainedRatherThanQuoted() {
        using var bundle = DoctorBundle.Create("bom");
        File.WriteAllText(bundle.At(BundleLayout.InfoPath), DoctorBundle.GoodInfo(), System.Text.Encoding.UTF8);

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.readable");
        Assert.Contains("byte-order mark", finding.Why, StringComparison.Ordinal);
        Assert.Contains("without a BOM", finding.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownSchemaMarkerIsAnError() {
        using var bundle = DoctorBundle.Create("schema", DoctorBundle.GoodInfo(schema: "trinix.bundle/9"));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Error, Single(report, "info.schema").Severity);
    }

    [Fact]
    public async Task AnIdentifierThatIsNotReverseDnsIsAnError() {
        using var bundle = DoctorBundle.Create("identity", DoctorBundle.GoodInfo(identifier: "hello"));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Error, Single(report, "info.identifier.form").Severity);

        // The advisory checks do not pile on to a name that is already refused.
        Silent(report, "info.identifier.labels");
        Silent(report, "info.identifier.characters");
    }

    [Fact]
    public async Task ATwoLabelIdentifierNamesADomainRatherThanAnApplication() {
        using var bundle = DoctorBundle.Create("labels", DoctorBundle.GoodInfo(identifier: "io.trinix"));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.identifier.labels");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("io.trinix.hello", finding.Fix!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("io.trinix.Hello", "uppercase")]
    [InlineData("io.trinix.my_app", "underscore")]
    [InlineData("io.trinix.9lives", "digit")]
    public async Task AnIdentifierThatIsLegalAndUnwiseIsAWarning(string identifier, string expected) {
        using var bundle = DoctorBundle.Create("chars", DoctorBundle.GoodInfo(identifier: identifier));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.identifier.characters");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(expected, finding.What, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A check that only exists because doctor knows what the sandbox does with an
    ///     identity: it becomes a transient unit's name, and systemd has a length limit.
    /// </summary>
    [Fact]
    public async Task AnIdentifierTooLongToNameAUnitIsAnError() {
        using var bundle = DoctorBundle.Create(
            "long",
            DoctorBundle.GoodInfo(identifier: "io.trinix." + new string('a', 250))
        );

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Error, Single(report, "info.identifier.length").Severity);
    }

    [Fact]
    public async Task AnEmptyNameIsAnError() {
        using var bundle = DoctorBundle.Create("noname", DoctorBundle.GoodInfo(name: " "));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Error, Single(report, "info.name.present").Severity);
    }

    [Fact]
    public async Task ANameThatDisagreesWithTheDirectoryIsAWarning() {
        using var bundle = DoctorBundle.Create("name", DoctorBundle.GoodInfo(name: "Greeting"));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.name.matches-directory");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("Greeting.app", finding.Fix!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The fix names the actual replacement, which is the difference between a
    ///     finding and a complaint.
    /// </summary>
    [Fact]
    public async Task AVersionThatIsNotDottedNumericIsAnErrorWithTheEditInIt() {
        using var bundle = DoctorBundle.Create("version", DoctorBundle.GoodInfo(version: "1.0.0-beta"));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.version.form");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("\"1.0.0\"", finding.Fix!, StringComparison.Ordinal);
        Assert.Contains("shortVersion", finding.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyShortVersionIsAWarning() {
        using var bundle = DoctorBundle.Create("short", DoctorBundle.GoodInfo(shortVersion: " "));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "info.version.short").Severity);
    }

    [Fact]
    public async Task AnAbsentMinimumSystemVersionIsAWarning() {
        using var bundle = DoctorBundle.Create("nomin", DoctorBundle.GoodInfo(minimumSystemVersion: null));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "info.minimum.declared").Severity);
    }

    /// <summary>
    ///     Doc 18 § R13, reported rather than resolved.
    /// </summary>
    /// <remarks>
    ///     ⚠ The finding says what the comparison will <i>do</i> — that <c>0.3-rc1</c>
    ///     is read as <c>0</c> — and does not say which of R13's two readings is right,
    ///     because a conformance tool that picked one would be deciding a versioning
    ///     policy by implementing it.
    /// </remarks>
    [Fact]
    public async Task APrereleaseMinimumIsReportedAsAmbiguousRatherThanResolved() {
        using var bundle = DoctorBundle.Create("r13", DoctorBundle.GoodInfo(minimumSystemVersion: "0.3-rc1"));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "info.minimum.form");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("compared as '0'", finding.What, StringComparison.Ordinal);
        Assert.Contains("R13", finding.Why, StringComparison.Ordinal);
    }

    /// <summary>The same suffix, one component deeper, truncates differently.</summary>
    [Fact]
    public async Task APrereleaseMinimumTruncatesDifferentlyAtDifferentDepths() {
        using var bundle = DoctorBundle.Create("r13-deep", DoctorBundle.GoodInfo(minimumSystemVersion: "0.3.1-rc1"));

        var report = await bundle.RunAsync(Options());

        Assert.Contains("compared as '0.3'", Single(report, "info.minimum.form").What, StringComparison.Ordinal);
    }

    /// <summary>R13 from the system's side: the machine, not the bundle, is the problem.</summary>
    [Fact]
    public async Task APrereleaseSystemVersionIsTheCaseR13SaysWillBiteFirst() {
        using var bundle = DoctorBundle.Create("r13-system");

        var report = await bundle.RunAsync(Options(system: "0.4-rc1"));

        var finding = Single(report, "info.minimum.system");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("0.4", finding.What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleThatNeedsANewerSystemThanThisOneIsANote() {
        using var bundle = DoctorBundle.Create("older", DoctorBundle.GoodInfo(minimumSystemVersion: "0.9"));

        var report = await bundle.RunAsync(Options(system: "0.3"));

        Assert.Equal(Severity.Note, Single(report, "info.minimum.system").Severity);
    }

    [Fact]
    public async Task WithNoOsReleaseTheSystemComparisonIsSkippedRatherThanPassed() {
        using var bundle = DoctorBundle.Create("nosystem");

        var report = await bundle.RunAsync(new DoctorOptions { Capabilities = Image, SystemVersion = null });

        if (SystemVersion.Current() is null) {
            Assert.Contains(report.Skipped, skipped => skipped.Check == "info.minimum.system");
        }
    }

    // --- permissions ----------------------------------------------------------

    [Fact]
    public async Task AnUnknownPermissionIsAnErrorBeforeSealRatherThanAt() {
        using var bundle = DoctorBundle.Create("perm", DoctorBundle.GoodInfo(permissions: "[\"camera\"]"));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "permissions.known");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("devices.camera", finding.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermissionDeclaredTwiceIsAWarning() {
        using var bundle = DoctorBundle.Create(
            "dupe",
            DoctorBundle.GoodInfo(permissions: "[\"display\", \"display\"]")
        );

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "permissions.duplicate").Severity);
    }

    /// <summary>
    ///     The one permission the vocabulary marks as never granted by a repository.
    /// </summary>
    [Fact]
    public async Task APermissionTheStoreNeverGrantsIsAWarningAndAStoreError() {
        using var bundle = DoctorBundle.Create(
            "reserved",
            DoctorBundle.GoodInfo(
                permissions: "[\"system.input\"]",
                usageDescriptions: "{ \"system.input\": \"To drive other applications for a screen reader.\" }"
            )
        );

        var relaxed = await bundle.RunAsync(Options());
        Assert.Equal(Severity.Warning, Single(relaxed, "permissions.store-reserved").Severity);
        Assert.True(relaxed.Ok);

        var strict = await bundle.RunAsync(Options(store: true));
        Assert.Equal(Severity.Error, Single(strict, "permissions.store-reserved").SeverityFor(true));
        Assert.False(strict.Ok);
    }

    /// <summary>Doc 04 § Consent: doctor warns, the Store requires.</summary>
    [Fact]
    public async Task APermissionWithNoUsageDescriptionIsAWarningAndAStoreError() {
        using var bundle = DoctorBundle.Create("usage", DoctorBundle.GoodInfo(permissions: "[\"files.home\"]"));

        var relaxed = await bundle.RunAsync(Options());
        var finding = Single(relaxed, "permissions.usage.present");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.True(finding.StoreGate);
        Assert.Contains("Access your files", finding.Why, StringComparison.Ordinal);

        var strict = await bundle.RunAsync(Options(store: true));
        Assert.False(strict.Ok);
    }

    /// <summary>
    ///     ⚠ <c>display</c> is the permission doc 04 marks "never shown", so there is no
    ///     dialog for a sentence to appear in and no warning for its absence.
    /// </summary>
    [Fact]
    public async Task ThePermissionThatIsNeverShownNeedsNoUsageDescription() {
        using var bundle = DoctorBundle.Create("display", DoctorBundle.GoodInfo(permissions: "[\"display\"]"));

        var report = await bundle.RunAsync(Options());

        Silent(report, "permissions.usage.present");
    }

    [Fact]
    public async Task AUsageDescriptionForAPermissionThatIsNeverShownIsAWarning() {
        using var bundle = DoctorBundle.Create(
            "shown",
            DoctorBundle.GoodInfo(
                permissions: "[\"display\"]",
                usageDescriptions: "{ \"display\": \"To draw a window.\" }"
            )
        );

        var report = await bundle.RunAsync(Options());

        Assert.Contains("never shown", Single(report, "permissions.usage.undeclared").What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsageDescriptionForAPermissionThatIsNotDeclaredIsAWarning() {
        using var bundle = DoctorBundle.Create(
            "orphan",
            DoctorBundle.GoodInfo(
                permissions: "[\"display\"]",
                usageDescriptions: "{ \"devices.camera\": \"To take a photograph.\" }"
            )
        );

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "permissions.usage.undeclared");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("does not declare", finding.What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsageDescriptionKeyedBySomethingThatIsNotAPermissionIsAWarning() {
        using var bundle = DoctorBundle.Create(
            "badkey",
            DoctorBundle.GoodInfo(usageDescriptions: "{ \"camera\": \"To take a photograph.\" }")
        );

        var report = await bundle.RunAsync(Options());

        Assert.Contains(
            "is not a permission",
            Single(report, "permissions.usage.undeclared").What,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ABundleWithNoDisplayPermissionCanNeverOpenAWindow() {
        using var bundle = DoctorBundle.Create("nodisplay");

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Note, Single(report, "permissions.display").Severity);
    }

    // --- contents -------------------------------------------------------------

    [Fact]
    public async Task ADirectoryWithoutTheAppSuffixIsAWarning() {
        using var bundle = DoctorBundle.Create("suffix", bundleName: "Hello");

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "contents.extension").Severity);
    }

    [Fact]
    public async Task AFileOutsideContentsIsAWarning() {
        using var bundle = DoctorBundle.Create("toplevel");
        bundle.Write("README.md", "read me\n");

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "contents.top-level");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("README.md", finding.Where);
    }

    /// <summary>
    ///     ⚠ Every symlink, not the first. The sealer throws on one, which is right for
    ///     a sealer and would make doctor a tool you run four times to fix four links.
    /// </summary>
    [Fact]
    public async Task EverySymlinkIsReportedAndTheEscapingOneIsNamedAsSuch() {
        using var bundle = DoctorBundle.Create("links");
        bundle.Write(BundleLayout.ResourcesDirectory + "/real.txt", "hello\n");
        bundle.Link(BundleLayout.ResourcesDirectory + "/inside.txt", "real.txt");
        bundle.Link(BundleLayout.ResourcesDirectory + "/outside.txt", "/etc/passwd");

        var report = await bundle.RunAsync(Options());

        var links = report.Findings.Where(finding => finding.Check == "contents.symlink").ToList();
        Assert.Equal(2, links.Count);
        Assert.All(links, finding => Assert.Equal(Severity.Error, finding.Severity));
        Assert.Contains(links, finding => finding.What.Contains("outside the bundle", StringComparison.Ordinal));
        Assert.Contains(links, finding => finding.Where == BundleLayout.ResourcesDirectory + "/inside.txt");
    }

    /// <summary>
    ///     ⚠ The one thing doctor knows that the signature cannot: the group and other
    ///     mode bits are outside the Merkle leaf on purpose, so nothing after sealing
    ///     will ever look at them again.
    /// </summary>
    [Fact]
    public async Task AWorldWritableFileIsAWarningTheSignatureCouldNeverCatch() {
        using var bundle = DoctorBundle.Create("writable");
        bundle.Write(BundleLayout.ResourcesDirectory + "/data.txt", "hello\n");
        bundle.Chmod(
            BundleLayout.ResourcesDirectory + "/data.txt",
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
        );

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "contents.writable");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("everyone", finding.What, StringComparison.Ordinal);
        Assert.Contains("0666", finding.What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingEntryPointIsAnErrorThatNamesTheCandidates() {
        using var bundle = DoctorBundle.Create("entry", DoctorBundle.GoodInfo(entryPoint: "Contents/Bin/absent"));

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "contents.entry-point.present");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("Contents/Bin/hello", finding.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryPointWithoutTheExecuteBitIsAnError() {
        using var bundle = DoctorBundle.Create("noexec");
        bundle.Chmod(BundleLayout.BinDirectory + "/hello", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "contents.entry-point.executable");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("chmod +x", finding.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryPointOutsideBinIsLegalAndAWarning() {
        using var bundle = DoctorBundle.Create(
            "elsewhere",
            DoctorBundle.GoodInfo(entryPoint: "Contents/Resources/run.sh")
        );

        bundle.Write(BundleLayout.ResourcesDirectory + "/run.sh", "#!/bin/sh\n", executable: true);

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "contents.entry-point.location").Severity);
    }

    /// <summary>The finding HelloUi.app produced the first time doctor was run on it.</summary>
    [Fact]
    public async Task AnEmptyDirectoryIsAWarningBecauseTheManifestListsFiles() {
        using var bundle = DoctorBundle.Create("empty");
        bundle.MakeDirectory(BundleLayout.ResourcesDirectory);

        var report = await bundle.RunAsync(Options());

        var finding = Single(report, "contents.empty-directory");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(BundleLayout.ResourcesDirectory, finding.Where);
    }

    /// <summary>⚠ A Mac-shaped mistake: Trinix is developed on one.</summary>
    [Fact]
    public async Task ADsStoreIsAWarning() {
        using var bundle = DoctorBundle.Create("dsstore");
        bundle.Write(BundleLayout.ResourcesDirectory + "/.DS_Store", "junk\n");

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "contents.stray").Severity);
    }

    [Fact]
    public async Task ADebugSymbolFileIsAWarning() {
        using var bundle = DoctorBundle.Create("pdb");
        bundle.Write(BundleLayout.BinDirectory + "/hello.pdb", "symbols\n");

        var report = await bundle.RunAsync(Options());

        Assert.Contains("hello.pdb", Single(report, "contents.stray").What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExecutableResourceIsAWarning() {
        using var bundle = DoctorBundle.Create("execres");
        bundle.Write(BundleLayout.ResourcesDirectory + "/tool.sh", "#!/bin/sh\n", executable: true);

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Warning, Single(report, "contents.resources.executable").Severity);
    }

    [Fact]
    public async Task AHalfPresentSignatureDirectoryIsAnError() {
        using var bundle = DoctorBundle.Create("halfsigned");
        await bundle.SealAsync();
        File.Delete(bundle.At(BundleLayout.SignaturePath));

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        Assert.Equal(Severity.Error, Single(report, "contents.signature-material").Severity);
    }

    /// <summary>
    ///     ⚠ Never a verdict. Doc 01 asks doctor to check the icon at every size and
    ///     nothing in the format says what an icon is, so the honest output is the
    ///     observation without the conclusion.
    /// </summary>
    [Fact]
    public async Task TheIconCheckIsANoteBecauseTheFormatDoesNotDefineOne() {
        using var bundle = DoctorBundle.Create("icon");

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Note, Single(report, "contents.icon").Severity);
    }

    // --- signature ------------------------------------------------------------

    [Fact]
    public async Task AnUnsealedBundleIsAWarningAndAStoreError() {
        using var bundle = DoctorBundle.Create("unsealed");

        var relaxed = await bundle.RunAsync(Options());
        var finding = Single(relaxed, "signature.sealed");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.True(finding.StoreGate);

        // And the checks that needed a signature say they were skipped rather than
        // saying nothing at all.
        Assert.Contains(relaxed.Skipped, skipped => skipped.Check == "signature.verify");

        Assert.False((await bundle.RunAsync(Options(store: true))).Ok);
    }

    [Fact]
    public async Task ATamperedFileIsAnErrorAndTheMessageNamesIt() {
        using var bundle = DoctorBundle.Create("tampered");
        await bundle.SealAsync();
        bundle.Write(BundleLayout.BinDirectory + "/hello", "#!/bin/sh\necho goodbye\n", executable: true);

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        var finding = Single(report, "signature.verify");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("re-seal", finding.Fix!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Skipped, not passed. There is no trust store on the machine that builds
    ///     bundles, and a clean report from a check that never ran is the one lie a
    ///     conformance tool must not tell.
    /// </summary>
    [Fact]
    public async Task WithNoTrustStoreTheVerificationIsSkippedAndSaysHowToRunIt() {
        using var bundle = DoctorBundle.Create("notrust");
        await bundle.SealAsync();

        var report = await bundle.RunAsync(
            new DoctorOptions { Capabilities = Image, TrustDirectory = Path.Combine(bundle.Scratch, "absent") }
        );

        var skipped = Assert.Single(report.Skipped, s => s.Check == "signature.verify");
        Assert.Contains("--trust", skipped.Reason, StringComparison.Ordinal);
        Silent(report, "signature.verify");
    }

    [Fact]
    public async Task AnExpiredSigningCertificateIsAnError() {
        using var bundle = DoctorBundle.Create(
            "expired",
            notBefore: DateTimeOffset.UtcNow.AddYears(-DeveloperPki.IdentityYears - 1)
        );

        await bundle.SealAsync(DateTimeOffset.UtcNow.AddYears(-DeveloperPki.IdentityYears));

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        var finding = Single(report, "signature.certificate");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("expired", finding.What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASigningCertificateAboutToExpireIsAWarning() {
        var almost = DateTimeOffset.UtcNow.AddYears(-DeveloperPki.IdentityYears).AddDays(20);
        using var bundle = DoctorBundle.Create("expiring", notBefore: almost);
        await bundle.SealAsync();

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        var finding = Single(report, "signature.certificate");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("expires", finding.What, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The extension that stops a TLS certificate from doubling as a code-signing
    ///     one. Without it the chain will not build, and the bundle reads as untrusted
    ///     rather than as mis-signed — which sends whoever is debugging it somewhere
    ///     else entirely.
    /// </summary>
    [Fact]
    public async Task ACertificateWithoutTheCodeSigningEkuIsAnError() {
        using var bundle = DoctorBundle.Create("noeku");
        await bundle.SealAsync();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Not For Code, O=Trinix", key, HashAlgorithmName.SHA256);
        using var plain = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1)
        );

        File.WriteAllText(bundle.At(BundleLayout.CertificatesPath), plain.ExportCertificatePem() + "\n");

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        Assert.Equal(Severity.Error, Single(report, "signature.code-signing-eku").Severity);
    }

    [Fact]
    public async Task CertificatesPemThatIsNotACertificateIsAnError() {
        using var bundle = DoctorBundle.Create("badpem");
        await bundle.SealAsync();
        File.WriteAllText(bundle.At(BundleLayout.CertificatesPath), "not a certificate\n");

        var report = await bundle.RunAsync(Options(bundle.TrustDirectory));

        Assert.Equal(Severity.Error, Single(report, "signature.certificate").Severity);
    }

    // --- sandbox --------------------------------------------------------------

    /// <summary>
    ///     The reason doctor is worth building: which declared permissions are enforced
    ///     by nothing on the system this will run on.
    /// </summary>
    [Fact]
    public async Task TheBrokerMediatedPermissionsAreReportedAsGaps() {
        using var bundle = DoctorBundle.Create(
            "gaps",
            DoctorBundle.GoodInfo(
                permissions: "[\"devices.camera\"]",
                usageDescriptions: "{ \"devices.camera\": \"To scan a document.\" }"
            )
        );

        var report = await bundle.RunAsync(Options());

        var gaps = report.Findings.Where(finding => finding.Check == "sandbox.gaps").ToList();
        Assert.Contains(gaps, gap => gap.What.StartsWith("devices.camera:", StringComparison.Ordinal));
        Assert.All(gaps, gap => Assert.Equal(Severity.Note, gap.Severity));
    }

    /// <summary>
    ///     The property that is set, read back by <c>systemctl show</c>, and enforcing
    ///     nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ A note, and the severity is the interesting assertion. It fires on every
    ///     bundle on the current image, identically, so as a warning it would be a
    ///     permanent two-line tax on every report — which is how the warning level
    ///     stops meaning anything.
    /// </remarks>
    [Fact]
    public async Task APropertyThatIsAcceptedAndInertIsANoteOnEveryBundleAlike() {
        using var bundle = DoctorBundle.Create("inert");

        var report = await bundle.RunAsync(Options());

        var inert = report.Findings.Where(finding => finding.Check == "sandbox.inert").ToList();
        Assert.NotEmpty(inert);
        Assert.All(inert, finding => Assert.Equal(Severity.Note, finding.Severity));
        Assert.Contains(inert, finding => finding.What.Contains("RestrictRealtime", StringComparison.Ordinal));
    }

    /// <summary>And on a systemd that has libseccomp, it is not reported at all.</summary>
    [Fact]
    public async Task OnASystemdWithSeccompNothingIsInert() {
        using var bundle = DoctorBundle.Create("seccomp");

        var report = await bundle.RunAsync(
            new DoctorOptions { Capabilities = SandboxCapabilities.Designed }
        );

        Assert.DoesNotContain(report.Findings, finding => finding.Check == "sandbox.inert");
    }

    [Fact]
    public async Task ABundleWithNoNetworkPermissionGetsNoNetworkStack() {
        using var bundle = DoctorBundle.Create("nonet");

        var report = await bundle.RunAsync(Options());

        Assert.Contains("PrivateNetwork=yes", Single(report, "sandbox.network").What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleWithNetworkClientJoinsTheSharedNamespace() {
        using var bundle = DoctorBundle.Create(
            "net",
            DoctorBundle.GoodInfo(
                permissions: "[\"network.client\"]",
                usageDescriptions: "{ \"network.client\": \"To fetch the day's greeting.\" }"
            )
        );

        var report = await bundle.RunAsync(Options());

        Assert.Contains(
            SandboxLayout.DefaultNetworkNamespace,
            Single(report, "sandbox.network").What,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     An entry point the launcher would refuse is an error here, not an exception.
    /// </summary>
    [Fact]
    public async Task ABundleTheSandboxCannotBeBuiltForIsAnError() {
        using var bundle = DoctorBundle.Create("unsafe", DoctorBundle.GoodInfo(entryPoint: "../escape"));

        var report = await bundle.RunAsync(Options());

        Assert.Equal(Severity.Error, Single(report, "sandbox.unit").Severity);
    }

    [Fact]
    public async Task AManagedBundleIsDetectedAndAnUnknownOneIsReported() {
        using var unknown = DoctorBundle.Create("native");
        var report = await unknown.RunAsync(Options());
        Assert.Equal(Severity.Note, Single(report, "sandbox.runtime").Severity);

        using var managed = DoctorBundle.Create("managed");
        managed.Write(BundleLayout.BinDirectory + "/hello.runtimeconfig.json", "{}\n");

        Assert.DoesNotContain(
            (await managed.RunAsync(Options())).Findings,
            finding => finding.Check == "sandbox.runtime"
        );
    }
}

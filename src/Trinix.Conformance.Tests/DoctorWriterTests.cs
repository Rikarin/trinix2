using Trinix.Sandbox;

namespace Trinix.Conformance.Tests;

/// <summary>
///     The rendering, which doc 01 makes the product rather than the packaging.
/// </summary>
public sealed class DoctorWriterTests {
    static async Task<DoctorReport> BrokenAsync(DoctorBundle bundle, bool store = false) =>
        await FindingShapeTests.SinkReportAsync(bundle, store);

    /// <summary>
    ///     The last line is the one a CI job and a submission pipeline read, so it is
    ///     one line, at the end, in the shape `trinix-bundle verify` already uses.
    /// </summary>
    [Fact]
    public async Task TheVerdictIsTheLastLineAndSaysWhichWay() {
        using var broken = FindingShapeTests.Sink("verdict");
        var text = DoctorWriter.ToText(await BrokenAsync(broken));

        var last = text.TrimEnd('\n').Split('\n')[^1];
        Assert.StartsWith(DoctorWriter.FailMarker, last, StringComparison.Ordinal);
        Assert.Contains("Broken.app", last, StringComparison.Ordinal);
        Assert.Contains("checks run", last, StringComparison.Ordinal);

        using var good = DoctorBundle.Create("verdict-ok");
        await good.SealAsync();
        var ok = DoctorWriter.ToText(await good.RunAsync(new DoctorOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            SystemVersion = "0.3",
            TrustDirectory = good.TrustDirectory
        }));

        Assert.StartsWith(DoctorWriter.PassMarker, ok.TrimEnd('\n').Split('\n')[^1], StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Errors and warnings are separated by a heading rather than by a word at the
    ///     start of a line, because the point of the distinction is that the two can be
    ///     read as two lists.
    /// </summary>
    [Fact]
    public async Task ErrorsAndWarningsAreSeparateSections() {
        using var broken = FindingShapeTests.Sink("sections");
        var text = DoctorWriter.ToText(await BrokenAsync(broken));

        var mustFix = text.IndexOf("must fix —", StringComparison.Ordinal);
        var shouldFix = text.IndexOf("should fix —", StringComparison.Ordinal);
        var information = text.IndexOf("for information —", StringComparison.Ordinal);

        Assert.True(mustFix >= 0 && shouldFix > mustFix && information > shouldFix, text);
    }

    /// <summary>
    ///     Every finding carries its three lines, and the labels are fixed so they can
    ///     be skimmed down the left.
    /// </summary>
    [Fact]
    public async Task EveryRenderedFindingCarriesItsWhyAndItsFix() {
        using var broken = FindingShapeTests.Sink("labels");
        var report = await BrokenAsync(broken);
        var text = DoctorWriter.ToText(report);

        Assert.Equal(
            report.Findings.Count,
            Occurrences(text, "\n      why  ")
        );

        Assert.Equal(
            report.Findings.Count(finding => finding.Fix is not null),
            Occurrences(text, "\n      fix  ")
        );

        static int Occurrences(string text, string needle) {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    ///     A warning doc 09 turns into a refusal says so where it is, rather than in a
    ///     footnote nobody reaches.
    /// </summary>
    [Fact]
    public async Task AStoreGateWarningSaysSoUntilStoreModeMakesItAnError() {
        using var broken = FindingShapeTests.Sink("gate");

        Assert.Contains(
            "doc 09 § Submission",
            DoctorWriter.ToText(await BrokenAsync(broken)),
            StringComparison.Ordinal
        );

        // Under --store it is already an error, so the hint would be telling the reader
        // something they are currently looking at.
        Assert.DoesNotContain(
            "doc 09 § Submission runs",
            DoctorWriter.ToText(await BrokenAsync(broken, store: true)),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ A fixed width, not the terminal's: this output is pasted into issues and
    ///     compared between runs, and text that reflows with the window diffs as noise.
    /// </summary>
    [Theory]
    [InlineData(72)]
    [InlineData(96)]
    [InlineData(120)]
    public async Task TheBodyWrapsAtTheWidthItWasGiven(int width) {
        using var broken = FindingShapeTests.Sink("width");
        var report = await BrokenAsync(broken);
        var text = DoctorWriter.ToText(report, width);

        foreach (var line in text.Split('\n')) {
            // The bundle's own path is printed whole — truncating the one thing a
            // reader might need to paste back would be a poor trade for a tidy column.
            if (line.Contains(report.BundlePath, StringComparison.Ordinal)) {
                continue;
            }

            // ⚠ And the verdict is never wrapped: it is what a pipeline greps for, and
            // half a verdict on a line is worse than a long one.
            if (line.StartsWith(DoctorWriter.PassMarker, StringComparison.Ordinal)
                || line.StartsWith(DoctorWriter.FailMarker, StringComparison.Ordinal)) {
                continue;
            }

            if (line.Length <= width) {
                continue;
            }

            // Otherwise the only excuse is a single token that does not fit at all.
            Assert.Contains(
                line.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                word => word.Length > width / 2
            );
        }
    }

    /// <summary>
    ///     Doc 01 lists twelve checks and this tool does not do twelve, so it says which
    ///     ones it does not — on every run, not in a README.
    /// </summary>
    [Fact]
    public async Task TheReportAlwaysSaysWhatItDoesNotCheck() {
        using var good = DoctorBundle.Create("honest");
        await good.SealAsync();

        var text = DoctorWriter.ToText(await good.RunAsync(new DoctorOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            SystemVersion = "0.3",
            TrustDirectory = good.TrustDirectory
        }));

        Assert.Contains("not checkable yet", text, StringComparison.Ordinal);
        Assert.Contains("the icon, at every size", text, StringComparison.Ordinal);
        Assert.NotEmpty(DoctorReport.NotChecked);
    }

    /// <summary>
    ///     ⚠ And which ones could not run <i>here</i>, which is the difference between a
    ///     clean report and an absent one.
    /// </summary>
    [Fact]
    public async Task TheReportSaysWhichChecksCouldNotRunOnThisMachine() {
        using var bundle = DoctorBundle.Create("skipped");
        await bundle.SealAsync();

        var text = DoctorWriter.ToText(await bundle.RunAsync(new DoctorOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            SystemVersion = "0.3",
            TrustDirectory = Path.Combine(bundle.Scratch, "no-such-directory")
        }));

        Assert.Contains("not checked here", text, StringComparison.Ordinal);
        Assert.Contains("signature.verify", text, StringComparison.Ordinal);
    }
}

using System.Globalization;
using System.Text;

namespace Trinix.Conformance;

/// <summary>
///     Turns a <see cref="DoctorReport" /> into the thing a person reads.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The output is the product.</b> A conformance tool whose message does
///         not say what to do is one people run once, and every decision in this file
///         is downstream of that: three fixed labels rather than prose, the check's id
///         beside every finding so it can be grepped and argued with, and the severity
///         as the outermost grouping so that <i>what will be refused</i> and <i>what
///         merely should not be</i> cannot be read as one list.
///     </para>
///     <para>
///         ⚠ The two closing sections are the ones a smaller tool would omit.
///         <i>not checked here</i> is what could not run on this machine — normally the
///         signature, because a build host has no trust store — and <i>not checkable
///         yet</i> is doc 01's list of promises this tool does not keep. Without the
///         first, a clean report cannot be told from an absent one; without the second,
///         doctor looks like it checks twelve things when it checks five of them.
///     </para>
///     <para>
///         The last line is a single grep-able verdict, in the shape
///         <c>trinix-bundle verify</c> already established with <c>BUNDLE-OK</c>. It is
///         what doc 16's CI job and doc 09's submission pipeline assert on, and it is
///         deliberately the last thing printed rather than the first: a person reads
///         down, and a machine reads the tail.
///     </para>
/// </remarks>
public static class DoctorWriter {
    /// <summary>Where the text wraps, when the caller does not say.</summary>
    /// <remarks>
    ///     ⚠ A fixed width rather than the terminal's. Doctor's output is pasted into
    ///     issues, captured by CI and compared between runs, and text that reflows with
    ///     the window is text whose diff is noise.
    /// </remarks>
    public const int DefaultWidth = 96;

    /// <summary>The verdict line's prefix when the bundle passed.</summary>
    public const string PassMarker = "DOCTOR-OK";

    /// <summary>The verdict line's prefix when it did not.</summary>
    public const string FailMarker = "DOCTOR-FAILED";

    /// <summary>Write the whole report.</summary>
    /// <param name="writer">Where it goes.</param>
    /// <param name="report">What to write.</param>
    /// <param name="width">Wrap column; <see cref="DefaultWidth" /> by default.</param>
    public static void Write(TextWriter writer, DoctorReport report, int width = DefaultWidth) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        Header(writer, report);

        Section(
            writer, report, width, Severity.Error,
            "must fix", "something will refuse this bundle"
        );

        Section(
            writer, report, width, Severity.Warning,
            "should fix", report.Store
                ? "the repository will not refuse these, and doc 09's reviewer reads them"
                : "these work today and should not"
        );

        Section(
            writer, report, width, Severity.Note,
            "for information", "facts about the system and the format, not defects in the bundle"
        );

        Skipped(writer, report, width);
        NotCheckable(writer, width);
        Verdict(writer, report);
    }

    /// <summary>The report as one string, for a test or a log.</summary>
    /// <param name="report">What to write.</param>
    /// <param name="width">Wrap column.</param>
    public static string ToText(DoctorReport report, int width = DefaultWidth) {
        var text = new StringWriter(CultureInfo.InvariantCulture);
        Write(text, report, width);
        return text.ToString();
    }

    static void Header(TextWriter writer, DoctorReport report) {
        // ⚠ The displayed version falls back to the ordered one when shortVersion is
        // blank. A header reading "Broken.app — io.example.broken" with nothing after it
        // looks like a rendering bug; the blank shortVersion is a finding below, and the
        // heading is not the place to report it.
        var identity = report.Info is { } info
            ? $"{info.Identifier} {(string.IsNullOrWhiteSpace(info.ShortVersion) ? info.Version : info.ShortVersion)}"
            : "unreadable";

        writer.WriteLine();
        writer.WriteLine($"{report.BundleName} — {identity}");
        writer.WriteLine($"  {report.BundlePath}");
    }

    static void Section(
        TextWriter writer,
        DoctorReport report,
        int width,
        Severity severity,
        string title,
        string subtitle
    ) {
        var findings = report.At(severity);
        if (findings.Count == 0) {
            return;
        }

        writer.WriteLine();
        Wrapped(writer, width, string.Empty, $"{title} — {Count(findings.Count, Word(severity))}, {subtitle}");

        foreach (var finding in findings) {
            writer.WriteLine();

            var head = "  " + finding.Check;
            if (finding.Where is { } where) {
                // Right-aligned when it fits, and on the same line either way: the
                // check's name and the file it is about are read together.
                var padding = width - head.Length - where.Length;
                head = padding > 1 ? head + new string(' ', padding) + where : head + "  " + where;
            }

            writer.WriteLine(head);
            Wrapped(writer, width, "    ", finding.What);
            Wrapped(writer, width, "      why  ", finding.Why);

            if (finding.Fix is { } fix) {
                Wrapped(writer, width, "      fix  ", fix);
            }

            if (finding.StoreGate && !report.Store) {
                Wrapped(
                    writer, width, "      ⚠    ",
                    "a repository refuses this: doc 09 § Submission runs `doctor --store`, where it "
                    + "is an error"
                );
            }
        }
    }

    static void Skipped(TextWriter writer, DoctorReport report, int width) {
        if (report.Skipped.Count == 0) {
            return;
        }

        writer.WriteLine();
        Wrapped(writer, width, string.Empty, "not checked here — these could not run on this machine");
        writer.WriteLine();

        foreach (var (check, reason) in report.Skipped) {
            Wrapped(writer, width, "  · " + check + " — ", reason);
        }
    }

    static void NotCheckable(TextWriter writer, int width) {
        writer.WriteLine();
        Wrapped(
            writer, width, string.Empty,
            "not checkable yet — doc 01 asks for these and nothing in the tree can answer them"
        );

        writer.WriteLine();

        foreach (var item in DoctorReport.NotChecked) {
            Wrapped(writer, width, "  · ", item);
        }
    }

    /// <summary>
    ///     ⚠ The one line that is never wrapped. It is what CI and a submission
    ///     pipeline grep for, and a verdict split across two lines is a verdict a
    ///     pipeline reads half of.
    /// </summary>
    static void Verdict(TextWriter writer, DoctorReport report) {
        var marker = report.Ok ? PassMarker : FailMarker;
        var counts = string.Join(", ", new[] {
            Count(report.Count(Severity.Error), "error"),
            Count(report.Count(Severity.Warning), "warning"),
            Count(report.Count(Severity.Note), "note")
        });

        var scope = report.Store ? " (--store)" : string.Empty;

        writer.WriteLine();
        writer.WriteLine(
            $"{marker} {report.BundleName}{scope} — {counts}; "
            + Count(report.ChecksRun.Count, "check") + " run, "
            + report.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped"
        );
    }

    static string Word(Severity severity) =>
        severity switch {
            Severity.Error => "error",
            Severity.Warning => "warning",
            _ => "note"
        };

    static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    /// <summary>
    ///     Write <paramref name="text" /> under <paramref name="label" />, wrapped, with
    ///     continuation lines lined up under the first.
    /// </summary>
    static void Wrapped(TextWriter writer, int width, string label, string text) {
        var indent = new string(' ', label.Length);
        var line = new StringBuilder(label);
        var first = true;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (!first && line.Length + 1 + word.Length > width) {
                writer.WriteLine(line.ToString());
                line.Clear().Append(indent);
                first = true;
            }

            if (!first) {
                line.Append(' ');
            }

            line.Append(word);
            first = false;
        }

        if (line.Length > indent.Length) {
            writer.WriteLine(line.ToString());
        }
    }
}

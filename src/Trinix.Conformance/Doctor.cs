using Trinix.Bundle;

namespace Trinix.Conformance;

/// <summary>
///     <c>trinix doctor</c> — the twelve things that are wrong with your bundle.
/// </summary>
/// <remarks>
///     <para>
///         Doc 01: "<c>trinix doctor</c> matters more than it looks. It is where
///         conformance stops being a document." Doc 09 § Submission makes a subset of
///         it a repository gate, and doc 16 runs it over Trinix's own applications in
///         CI — which is the arrangement that keeps it honest, because a conformance
///         tool whose author's own applications fail it is one whose rules get argued
///         with rather than quietly relaxed.
///     </para>
///     <para>
///         ⚠ <b>Scoped to what is checkable today, and it says what is not.</b> Doc 01
///         lists more than exists: half its checks need an SDK that can see an
///         application's service calls, a menu model, a string catalogue or an icon
///         format, and none of those are in the tree. Those are not silently omitted —
///         <see cref="DoctorReport.NotChecked" /> is printed on every run, because a
///         tool that checked half of a documented list while looking like it checked
///         all of it would make a green result mean something it does not.
///     </para>
///     <para>
///         ⚠ Nothing here throws for a bad bundle. Every refusal this can discover is a
///         <see cref="Finding" />, because the caller is a report and not a launcher —
///         and an exception halfway through means the developer fixes one thing and
///         runs it again to find the next.
///     </para>
/// </remarks>
public static class Doctor {
    /// <summary>
    ///     Examine a bundle and report everything wrong with it.
    /// </summary>
    /// <param name="bundlePath">The <c>.app</c> directory. It need not be sealed.</param>
    /// <param name="options">Trust store, store mode, and the machine-dependent inputs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="IOException">The bundle directory could not be read at all.</exception>
    public static async Task<DoctorReport> RunAsync(
        string bundlePath,
        DoctorOptions? options = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(bundlePath);
        options ??= new DoctorOptions();

        var context = new DoctorContext();
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundlePath)));

        // The one finding that stops the run. Everything below reads Info.json, so
        // there is nothing to say about a bundle that has none — and saying it thirty
        // times would bury the one line that matters.
        context.Ran("info.readable");
        BundleInfo info;
        try {
            info = await BundleSealer.ReadInfoAsync(bundlePath, cancellationToken).ConfigureAwait(false);
        } catch (BundleException e) {
            var (why, fix) = ExplainUnreadable(e, bundlePath);
            context.Error("info.readable", e.Message, why, fix, BundleLayout.InfoPath);

            return new DoctorReport {
                BundlePath = bundlePath,
                BundleName = name,
                Findings = [.. context.Findings],
                ChecksRun = [.. context.ChecksRun],
                Skipped = [.. context.Skipped],
                Store = options.Store
            };
        }

        var entries = BundleContents.Walk(bundlePath, out var walkProblem);

        InfoChecks.Run(context, info, bundlePath, options);
        PermissionChecks.Run(context, info);
        ContentsChecks.Run(context, info, bundlePath, entries, walkProblem);
        await SignatureChecks.RunAsync(context, info, bundlePath, options, cancellationToken).ConfigureAwait(false);
        SandboxChecks.Run(context, info, bundlePath, entries, options);

        return new DoctorReport {
            BundlePath = bundlePath,
            BundleName = name,
            Info = info,
            Findings = [.. context.Findings],
            ChecksRun = [.. context.ChecksRun],
            Skipped = [.. context.Skipped],
            Store = options.Store
        };
    }

    /// <summary>
    ///     Turn the reason <c>Info.json</c> could not be read into one somebody can act
    ///     on.
    /// </summary>
    /// <remarks>
    ///     ⚠ The byte-order-mark branch is the whole reason this is a method. A file
    ///     saved as "UTF-8 with BOM" — which several editors do by default, and which
    ///     .NET's own <c>File.WriteAllText(path, text, Encoding.UTF8)</c> does — reaches
    ///     the parser as <c>'0xEF' is an invalid start of a value. Path: $ | LineNumber:
    ///     0</c>, which reads as "your JSON is broken at the very first character" about
    ///     a file that looks perfect in every editor. It is the kind of half-hour nobody
    ///     should spend twice, and three bytes are cheap to look at.
    /// </remarks>
    static (string Why, string Fix) ExplainUnreadable(BundleException failure, string bundlePath) {
        if (failure.Failure == BundleFailure.NotABundle) {
            return (
                $"a bundle is a directory containing {BundleLayout.InfoPath}; without it nothing — "
                + "the installer, the launcher, doctor — can say what this is",
                $"create {BundleLayout.InfoPath} with schema, identifier, name, version and entryPoint"
            );
        }

        if (HasByteOrderMark(Path.Combine(bundlePath, BundleLayout.InfoPath))) {
            return (
                "the file begins with a UTF-8 byte-order mark, and the parser reads those three "
                + "bytes as the first character of the document — so a file that looks perfect in "
                + "every editor is refused at offset zero",
                "save it as UTF-8 without a BOM. ⚠ .NET's own "
                + "File.WriteAllText(path, text, Encoding.UTF8) writes one, which is how this "
                + "usually gets in"
            );
        }

        return (
            "Info.json is read before any signature is looked at, so a file that will not parse is "
            + "refused as malformed rather than as untrusted",
            $"fix the JSON in {BundleLayout.InfoPath}; the message above is the parser's, and it "
            + "names the offset"
        );
    }

    static bool HasByteOrderMark(string path) {
        try {
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            return file.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
                && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }
    }
}

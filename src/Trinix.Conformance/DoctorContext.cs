namespace Trinix.Conformance;

/// <summary>
///     What a check writes into while it runs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <see cref="Ran" /> is called by every check, including the ones that find
///         nothing, and that is a deliberate piece of bookkeeping rather than
///         ceremony. It is what lets the summary line say <i>31 checks, 4 errors</i>
///         instead of <i>4 errors</i> — and the difference matters most on the run
///         where nothing is reported, because a clean report and a report from a tool
///         that skipped everything look identical otherwise.
///     </para>
///     <para>
///         The helpers below take <c>what</c>, <c>why</c> and <c>fix</c> as separate
///         arguments for the reason <see cref="Finding" /> gives: a single
///         <c>message</c> parameter is an invitation to write only the first of the
///         three, and the other two are the ones with any value in them.
///     </para>
/// </remarks>
sealed class DoctorContext {
    readonly List<Finding> _findings = [];
    readonly List<string> _ran = [];
    readonly List<SkippedCheck> _skipped = [];

    internal IReadOnlyList<Finding> Findings => _findings;

    internal IReadOnlyList<string> ChecksRun => _ran;

    internal IReadOnlyList<SkippedCheck> Skipped => _skipped;

    /// <summary>This check was asked. Call it once, whatever the answer.</summary>
    internal void Ran(string check) {
        if (!_ran.Contains(check, StringComparer.Ordinal)) {
            _ran.Add(check);
        }
    }

    /// <summary>This check could not be asked here.</summary>
    internal void Skip(string check, string reason) => _skipped.Add(new SkippedCheck(check, reason));

    /// <summary>Something will refuse this bundle.</summary>
    internal void Error(string check, string what, string why, string fix, string? where = null) =>
        Add(new Finding {
            Check = check, Severity = Severity.Error, What = what, Why = why, Fix = fix, Where = where
        });

    /// <summary>
    ///     This will work, and should not. <c>storeGate</c> marks the ones doc 09 §
    ///     Submission turns into errors for a repository.
    /// </summary>
    internal void Warn(
        string check,
        string what,
        string why,
        string fix,
        string? where = null,
        bool storeGate = false
    ) =>
        Add(new Finding {
            Check = check,
            Severity = Severity.Warning,
            What = what,
            Why = why,
            Fix = fix,
            Where = where,
            StoreGate = storeGate
        });

    /// <summary>A fact about the system or the format. Never fails a run.</summary>
    internal void Note(string check, string what, string why, string? fix = null, string? where = null) =>
        Add(new Finding {
            Check = check, Severity = Severity.Note, What = what, Why = why, Fix = fix, Where = where
        });

    void Add(Finding finding) {
        Ran(finding.Check);
        _findings.Add(finding);
    }
}

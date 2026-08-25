namespace Trinix.Conformance;

/// <summary>
///     How much a finding matters.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three levels, and the distinction between the first two is the whole
///         design.</b> A conformance tool that reports everything at one severity is a
///         tool whose output gets skimmed, and then ignored, and then removed from CI
///         because it is always red. So the boundary is drawn at a question with an
///         objective answer — <i>will something refuse this bundle?</i> — rather than
///         at how strongly the check's author feels.
///     </para>
///     <para>
///         <see cref="Note" /> is the third because most of what doctor knows about
///         the sandbox is not a defect in the bundle at all. Ten of the fourteen
///         permissions are enforced by a broker that does not exist yet; saying so is
///         worth saying, and calling it a warning would be blaming a developer for the
///         state of the system.
///     </para>
/// </remarks>
public enum Severity {
    /// <summary>
    ///     A fact about the system or the format that the developer should know and
    ///     cannot act on. Never affects the exit code.
    /// </summary>
    Note,

    /// <summary>
    ///     This will work, and should not. Nothing refuses the bundle for it today.
    /// </summary>
    /// <remarks>
    ///     ⚠ A warning that carries <see cref="Finding.StoreGate" /> becomes an
    ///     <see cref="Error" /> under <c>--store</c>, which is doc 09's
    ///     "the repository operator runs <c>trinix doctor --store</c>". That escalation
    ///     is the only thing <c>--store</c> does, and it is what makes the flag mean
    ///     something rather than being a second opinion.
    /// </remarks>
    Warning,

    /// <summary>
    ///     Something will refuse this bundle: the sealer, the verifier, the launcher,
    ///     or the repository.
    /// </summary>
    Error
}

/// <summary>
///     One thing doctor found, and what to do about it.
/// </summary>
/// <remarks>
///     ⚠ <see cref="What" />, <see cref="Why" /> and <see cref="Fix" /> are three
///     fields rather than one message, because a message is where the last two go to
///     die. "version '1.0-beta' is not dotted-numeric" is a true sentence that leaves
///     a developer exactly where they started; the reason it matters and the edit that
///     ends it are the part worth having, and a shape that makes them optional makes
///     them absent. <c>FindingShapeTests</c> asserts that every warning and error this
///     library can produce carries a fix.
/// </remarks>
public sealed record Finding {
    /// <summary>
    ///     The check's stable identity, dotted, e.g. <c>info.version.form</c>.
    /// </summary>
    /// <remarks>
    ///     Stable because CI and a repository's submission pipeline will end up
    ///     naming individual checks — to waive one, to grep for one, to explain a
    ///     rejection. The prefix before the first dot is the section it is reported
    ///     under; see <see cref="Section" />.
    /// </remarks>
    public required string Check { get; init; }

    /// <summary>How much it matters.</summary>
    public required Severity Severity { get; init; }

    /// <summary>What is wrong, in one line, naming the actual value.</summary>
    public required string What { get; init; }

    /// <summary>Why that matters, in one line. Not a lecture.</summary>
    public required string Why { get; init; }

    /// <summary>
    ///     The concrete edit that ends it, or <see langword="null" /> when there is
    ///     nothing the developer can do.
    /// </summary>
    /// <remarks>
    ///     ⚠ Nullable only for <see cref="Severity.Note" />. A warning or an error
    ///     without a fix is the failure mode this whole type exists to prevent.
    /// </remarks>
    public string? Fix { get; init; }

    /// <summary>
    ///     Where to look: a bundle-relative path, or <see langword="null" /> when the
    ///     finding is about the bundle as a whole.
    /// </summary>
    public string? Where { get; init; }

    /// <summary>
    ///     Is this one of the checks doc 09 § Submission makes a repository gate?
    /// </summary>
    /// <remarks>
    ///     Only meaningful on a <see cref="Severity.Warning" />: under <c>--store</c> it
    ///     is reported as an error instead. An error is a gate everywhere already.
    /// </remarks>
    public bool StoreGate { get; init; }

    /// <summary>The part of the bundle this is about — the check id up to its first dot.</summary>
    public string Section {
        get {
            var dot = Check.IndexOf('.', StringComparison.Ordinal);
            return dot < 0 ? Check : Check[..dot];
        }
    }

    /// <summary>
    ///     How this reads to whoever is asking.
    /// </summary>
    /// <param name="store">
    ///     Whether the caller is a repository running doc 09's submission gate.
    /// </param>
    public Severity SeverityFor(bool store) =>
        store && StoreGate && Severity == Severity.Warning ? Severity.Error : Severity;
}

/// <summary>
///     A check doctor could not run, and why not.
/// </summary>
/// <remarks>
///     ⚠ Reported rather than silently omitted, and the difference is the point. A
///     signature check that did not run because there is no trust store on this
///     machine, and a signature check that ran and passed, produce the same silence —
///     and a developer reading a clean report is entitled to know which one they got.
/// </remarks>
/// <param name="Check">The check's id.</param>
/// <param name="Reason">One line, and where possible what to pass to make it run.</param>
public readonly record struct SkippedCheck(string Check, string Reason);

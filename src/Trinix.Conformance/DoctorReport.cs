using Trinix.Bundle;

namespace Trinix.Conformance;

/// <summary>
///     What one conformance run concluded.
/// </summary>
/// <remarks>
///     Data, not output. <see cref="DoctorWriter" /> turns it into the thing a person
///     reads and <c>Trinix.Conformance.Tests</c> asserts on it directly, which is what
///     makes "this check fires on this bundle" a test rather than a substring search
///     over a rendered page.
/// </remarks>
public sealed record DoctorReport {
    /// <summary>The bundle that was examined.</summary>
    public required string BundlePath { get; init; }

    /// <summary>Its directory name, e.g. <c>Hello.app</c>.</summary>
    public required string BundleName { get; init; }

    /// <summary>Its <c>Info.json</c>, when that could be read at all.</summary>
    public BundleInfo? Info { get; init; }

    /// <summary>Everything found, in the order the checks ran.</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    /// <summary>Every check that ran, whether or not it found anything.</summary>
    /// <remarks>
    ///     Kept so the summary can say "31 checks" honestly. A tool that reports how
    ///     many problems it found and not how many questions it asked is one whose
    ///     silence cannot be interpreted.
    /// </remarks>
    public IReadOnlyList<string> ChecksRun { get; init; } = [];

    /// <summary>Every check that could not run here, and why not.</summary>
    public IReadOnlyList<SkippedCheck> Skipped { get; init; } = [];

    /// <summary>Was this doc 09's submission subset?</summary>
    public bool Store { get; init; }

    /// <summary>How many findings read at this severity, given <see cref="Store" />.</summary>
    /// <param name="severity">The severity to count.</param>
    public int Count(Severity severity) {
        var total = 0;
        foreach (var finding in Findings) {
            if (finding.SeverityFor(Store) == severity) {
                total++;
            }
        }

        return total;
    }

    /// <summary>Findings at one severity, in check order.</summary>
    /// <param name="severity">The severity to select.</param>
    public IReadOnlyList<Finding> At(Severity severity) =>
        [.. Findings.Where(finding => finding.SeverityFor(Store) == severity)];

    /// <summary>Did the bundle pass?</summary>
    /// <remarks>
    ///     ⚠ Errors only. A warning that failed the run would make the severity
    ///     distinction cosmetic, and the first person to hit one would ask for a flag
    ///     to turn it off — which is how a conformance tool becomes a tool that is
    ///     always run with <c>--no-checks</c>.
    /// </remarks>
    public bool Ok => Count(Severity.Error) == 0;

    /// <summary>
    ///     What doctor does not check, and why not — printed on every run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Part of the output, not a README.</b> Doc 01 lists twelve things
    ///         doctor should check and roughly half of them need something that does not
    ///         exist yet: an SDK that can see the application's service calls, a menu
    ///         model, a string catalogue, a focus order, an icon format. A tool that
    ///         quietly checked the other half would leave every reader believing it
    ///         checked all twelve, and a green run would mean something it does not.
    ///     </para>
    ///     <para>
    ///         Each line names the doc 01 promise and the thing that has to exist first.
    ///         The list shrinks as those arrive, which is also how it doubles as a
    ///         worklist.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> NotChecked { get; } = [
        "permissions against the code's service calls — doc 01 says \"the generator knows both\"; "
        + "Trinix.Sdk.Generators emits service proxies today but nothing records which permission a "
        + "call needs, so there is no second opinion to compare the declaration against",

        "the menu's standard items — there is no menu model in the tree yet; doc 01 § Open has not "
        + "settled whether a menu is declared in .vxml or built in C#, and a check would have to "
        + "pick one",

        "whether every string is localisable — doc 20 puts the catalogue in Phase 7. Finding the "
        + "strings that are *not* in one needs the catalogue to exist and the SDK to know which "
        + "call sites take a message id",

        "keyboard reachability — a focus-order check needs a running window and the accessibility "
        + "tree doc 46 of Vixen has not built; it is a test-harness question rather than a "
        + "static one",

        "the icon, at every size — Info.json has no icon field and the format defines no path or "
        + "size set for one. Checking for a convention nobody has written down would be inventing "
        + "the format inside the tool that validates it",

        "whether minimumSystemVersion is *right* — doctor checks its form and reports how it will "
        + "be compared, but \"is not a guess\" is a claim about what the application actually calls, "
        + "and that is the SDK's cross-check above",

        "device nodes, sockets and fifos inside the bundle — .NET's filesystem API cannot tell one "
        + "from a regular file. BundleScanner has the same blind spot and says so; the defence is "
        + "that a bundle is produced by a build rather than assembled by hand"
    ];
}

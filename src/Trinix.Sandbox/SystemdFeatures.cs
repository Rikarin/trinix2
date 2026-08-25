using System.Diagnostics.CodeAnalysis;

namespace Trinix.Sandbox;

/// <summary>
///     What a systemd binary says it was built with, parsed out of its own version
///     banner.
/// </summary>
/// <remarks>
///     <para>
///         <c>systemctl --version</c> and <c>systemd-analyze --version</c> both print a
///         version line and then a feature string: a run of tokens, each a feature name
///         with a <c>+</c> or a <c>-</c> in front of it, ending in a few
///         <c>key=value</c> settings. It is the one place a systemd states its own build
///         configuration, and it is generated from the same <c>#if</c>s that decide
///         which properties exist.
///     </para>
///     <para>
///         ⚠ <b>This exists because the obvious probe is a liar.</b> The natural way to
///         ask "does this systemd have <c>SystemCallFilter=</c>" is
///         <c>systemctl show -p SystemCallFilter</c>, and it answers
///         <c>SystemCallFilter=~</c> — a perfectly well-formed value, for the property
///         that fails the call when a transient unit tries to set it. <c>show</c>
///         distinguishes properties systemd knows the <i>name</i> of from ones it does
///         not, which is a different question from the one being asked, and on the
///         Trinix image it gets all five seccomp properties wrong in the direction that
///         produces a sandbox which looks armed. The feature string gets them right
///         because <c>-SECCOMP</c> is the actual cause.
///     </para>
///     <para>
///         ⚠ The same string carries several other absences that this repository assumes
///         away elsewhere. The image reads <c>-PAM -AUDIT -SELINUX -ACL -OPENSSL -TPM2
///         -PCRE2 -BPF_FRAMEWORK</c>; doc 05 builds authentication on PAM and keychain
///         sealing on TPM2, and doc 10 re-seals a TPM policy across updates. Those are
///         libraries Trinix can link for itself, so the documents are not wrong — but
///         nothing comes for free from systemd, and <see cref="Present" /> and
///         <see cref="Absent" /> are whole rather than reduced to a
///         <see cref="Seccomp" /> flag so that the next component to care can ask
///         without a second parser.
///     </para>
///     <para>
///         Parsing is a pure function of a string, which is the entire reason it lives
///         apart from <see cref="SystemdProbe" />: the behaviour worth testing is "this
///         banner means seccomp is missing", and that is testable against a captured
///         string on a machine with no systemd on it at all.
///     </para>
/// </remarks>
public sealed record SystemdFeatures {
    /// <summary>The feature name that gates doc 04's syscall floor.</summary>
    public const string Seccomp = "SECCOMP";

    /// <summary>The word every version banner starts with.</summary>
    const string Banner = "systemd";

    /// <summary>
    ///     A systemd whose banner said nothing useful.
    /// </summary>
    /// <remarks>
    ///     ⚠ Distinct from "a systemd without seccomp", and the difference reaches the
    ///     user: <see cref="SandboxCapabilities.From(SystemdFeatures)" /> treats an
    ///     unmentioned feature as absent, so this produces the same <i>properties</i> as
    ///     a measured <c>-SECCOMP</c> — but a refusal or a journal line can still say
    ///     "we could not read the version banner" rather than claiming a measurement it
    ///     did not make.
    /// </remarks>
    public static SystemdFeatures Unknown { get; } = new() {
        Version = 0,
        VersionText = string.Empty,
        Present = new HashSet<string>(StringComparer.Ordinal),
        Absent = new HashSet<string>(StringComparer.Ordinal)
    };

    /// <summary>The major version, e.g. 257. Zero when the banner did not say.</summary>
    public required int Version { get; init; }

    /// <summary>The first line, verbatim, for quoting in a diagnostic.</summary>
    public required string VersionText { get; init; }

    /// <summary>Every feature the string marked with a <c>+</c>, upper-cased.</summary>
    public required IReadOnlySet<string> Present { get; init; }

    /// <summary>Every feature the string marked with a <c>-</c>, upper-cased.</summary>
    public required IReadOnlySet<string> Absent { get; init; }

    /// <summary>
    ///     Was this built with <paramref name="feature" />?
    /// </summary>
    /// <param name="feature">A feature name such as <c>SECCOMP</c>, in any case.</param>
    /// <returns>
    ///     <see langword="true" /> or <see langword="false" /> when the string named it,
    ///     and <see langword="null" /> when it did not.
    /// </returns>
    /// <remarks>
    ///     ⚠ Three answers, and the third is not pedantry. A systemd older than a
    ///     feature does not print <c>-THING</c>; it prints nothing at all, exactly as a
    ///     banner this parser failed to read does. Collapsing that into
    ///     <see langword="false" /> here would put the decision about what silence means
    ///     inside a parser, where no caller can see it — so it is returned as
    ///     <see langword="null" /> and
    ///     <see cref="SandboxCapabilities.From(SystemdFeatures)" /> makes the call, in
    ///     the safe direction, with a comment saying why.
    /// </remarks>
    public bool? Has(string feature) {
        ArgumentException.ThrowIfNullOrEmpty(feature);

        var name = feature.ToUpperInvariant();
        if (Present.Contains(name)) {
            return true;
        }

        return Absent.Contains(name) ? false : null;
    }

    /// <summary>
    ///     Read a version banner.
    /// </summary>
    /// <param name="banner">
    ///     The whole of what <c>systemctl --version</c> or
    ///     <c>systemd-analyze --version</c> printed.
    /// </param>
    /// <param name="features">What it said, when it looked like a banner at all.</param>
    /// <param name="problem">One line saying why not, otherwise.</param>
    /// <returns><see langword="true" /> when <paramref name="features" /> is set.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Tokens, not columns.</b> The parser takes every whitespace-separated
    ///         token beginning with <c>+</c> or <c>-</c> whose remainder is an
    ///         identifier, wherever it appears, and ignores everything else. It is
    ///         written that way because the one thing that is certain about this string
    ///         is that its contents change: features are added and removed every
    ///         release, the trailing <c>key=value</c> settings come and go, and the
    ///         line has wrapped differently in different versions. A parser that
    ///         depended on the count, the order, or the line number of anything would
    ///         be a parser that breaks on a systemd upgrade — and it would break by
    ///         reporting that the build has no seccomp, which is a silent downgrade of
    ///         the sandbox rather than an error.
    ///     </para>
    ///     <para>
    ///         The identifier rule is what keeps <c>(257.2-1)</c> out of
    ///         <see cref="Absent" />: version suffixes contain dashes, and
    ///         <c>-1)</c> is not a feature name.
    ///     </para>
    /// </remarks>
    public static bool TryParse(
        string? banner,
        [NotNullWhen(true)] out SystemdFeatures? features,
        out string? problem
    ) {
        features = null;

        if (string.IsNullOrWhiteSpace(banner)) {
            problem = "systemd printed no version banner at all";
            return false;
        }

        var lines = banner.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var versionText = lines[0];

        if (!versionText.StartsWith(Banner, StringComparison.Ordinal)) {
            problem = $"'{Truncate(versionText)}' is not a systemd version banner";
            return false;
        }

        HashSet<string> present = new(StringComparer.Ordinal);
        HashSet<string> absent = new(StringComparer.Ordinal);

        foreach (var token in banner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
            var sign = token[0];
            if (sign is not ('+' or '-')) {
                continue;
            }

            var name = token[1..];
            if (!IsFeatureName(name)) {
                continue;
            }

            _ = (sign == '+' ? present : absent).Add(name.ToUpperInvariant());
        }

        features = new SystemdFeatures {
            Version = MajorVersion(versionText),
            VersionText = versionText,
            Present = present,
            Absent = absent
        };

        problem = null;
        return true;
    }

    /// <summary>Read a version banner, or refuse.</summary>
    /// <param name="banner">What <c>systemctl --version</c> printed.</param>
    /// <exception cref="FormatException">It was not a systemd version banner.</exception>
    public static SystemdFeatures Parse(string banner) =>
        TryParse(banner, out var features, out var problem)
            ? features
            : throw new FormatException(problem);

    /// <summary>The first run of digits after the word <c>systemd</c>, or zero.</summary>
    /// <remarks>
    ///     ⚠ Zero rather than a throw when the version is unreadable. Nothing in this
    ///     slice gates on the number — <see cref="SandboxCapabilities.PrivatePids" /> is
    ///     a judgement rather than a version check — so refusing to launch over an
    ///     unparseable version would be refusing over a field nobody reads.
    /// </remarks>
    static int MajorVersion(string versionText) {
        var index = Banner.Length;
        while (index < versionText.Length && !char.IsAsciiDigit(versionText[index])) {
            index++;
        }

        var start = index;
        while (index < versionText.Length && char.IsAsciiDigit(versionText[index])) {
            index++;
        }

        return index > start && int.TryParse(versionText.AsSpan(start, index - start), out var version) ? version : 0;
    }

    /// <summary>
    ///     Does this look like a feature name rather than the tail of a version string?
    /// </summary>
    static bool IsFeatureName(ReadOnlySpan<char> name) {
        if (name.IsEmpty) {
            return false;
        }

        foreach (var character in name) {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') {
                return false;
            }
        }

        // A leading digit means a version fragment — "-1)" has already been rejected
        // by the punctuation rule, but "-2" would not have been.
        return !char.IsAsciiDigit(name[0]);
    }

    static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
}

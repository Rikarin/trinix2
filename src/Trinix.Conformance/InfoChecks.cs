using System.Globalization;
using Trinix.Bundle;
using Trinix.Sandbox;

namespace Trinix.Conformance;

/// <summary>
///     <c>Info.json</c>, beyond what <see cref="BundleInfo.Validate" /> refuses.
/// </summary>
/// <remarks>
///     <para>
///         The overlap with <c>Validate</c> is intentional and is not duplication:
///         where <c>Validate</c> returns a sentence, doctor returns the same verdict
///         with the reason and the edit attached, and it reaches the developer before
///         the sealer does rather than after. The <i>predicates</i> are not duplicated
///         — <see cref="BundleInfo.IsReverseDnsIdentifier" /> and
///         <see cref="BundleInfo.IsOrderedVersion" /> are the same code both sides
///         call, so doctor cannot pass something the sealer will refuse.
///     </para>
///     <para>
///         What is genuinely new here is everything <c>Validate</c> is right not to
///         refuse: an identity that is legal and unwise, a version field with no
///         <c>shortVersion</c> beside it, and the whole of the
///         <c>minimumSystemVersion</c> story — which doctor <i>reports</i> rather than
///         resolves, because doc 18 § R13 records the comparison as pinned and
///         undecided, and a conformance tool that picked a side would be making the
///         decision by implementing it.
///     </para>
/// </remarks>
static class InfoChecks {
    /// <summary>Longest unit name systemd will accept, less its <c>.service</c> suffix.</summary>
    /// <remarks>
    ///     systemd's limit is the filesystem's <c>NAME_MAX</c>: 255 bytes for the whole
    ///     unit name. The bundle identifier is only part of it — see
    ///     <see cref="SandboxUnitBuilder.UnitNameFor" /> — so this is a check about the
    ///     identity that only exists because the sandbox turns it into a unit name.
    /// </remarks>
    internal const int MaximumUnitNameLength = 255;

    internal static void Run(DoctorContext context, BundleInfo info, string bundlePath, DoctorOptions options) {
        Schema(context, info);
        Identifier(context, info);
        Name(context, info, bundlePath);
        Version(context, info);
        Minimum(context, info, options);
    }

    static void Schema(DoctorContext context, BundleInfo info) {
        context.Ran("info.schema");

        if (info.Schema == BundleSchema.Info) {
            return;
        }

        context.Error(
            "info.schema",
            $"schema is '{info.Schema}', not '{BundleSchema.Info}'",
            "the sealer and the verifier both refuse a document whose format marker they do not "
            + "recognise, which is what stops a corrupt file being read as an older revision",
            $"set \"schema\": \"{BundleSchema.Info}\"",
            BundleLayout.InfoPath
        );
    }

    static void Identifier(DoctorContext context, BundleInfo info) {
        context.Ran("info.identifier.form");

        if (!BundleInfo.IsReverseDnsIdentifier(info.Identifier)) {
            context.Error(
                "info.identifier.form",
                $"identifier '{info.Identifier}' is not a reverse-DNS name",
                "it is the application's identity everywhere: the container directory, the transient "
                + "unit name the broker attributes requests to, and the receipt. `trinix-bundle seal` "
                + "refuses the bundle",
                "use a domain you control, reversed, plus the application: \"io.example.notes\"",
                BundleLayout.InfoPath
            );

            // Everything below is advice about a name that is already refused.
            return;
        }

        var labels = info.Identifier.Split('.');

        context.Ran("info.identifier.labels");
        if (labels.Length < 3) {
            context.Warn(
                "info.identifier.labels",
                $"identifier '{info.Identifier}' has {labels.Length.ToString(CultureInfo.InvariantCulture)} labels",
                "a two-label identity names a domain rather than an application, so the second "
                + "application from the same developer has nowhere to go without renaming the first — "
                + "and an identity is not renameable once anything is installed under it",
                $"add the application: \"{info.Identifier}.{Slug(info.Name)}\"",
                BundleLayout.InfoPath
            );
        }

        context.Ran("info.identifier.characters");
        List<string> awkward = [];
        foreach (var label in labels) {
            if (label.Contains('_', StringComparison.Ordinal)) {
                awkward.Add($"'{label}' contains an underscore, which is not legal in a DNS label");
            }

            if (char.IsAsciiDigit(label[0])) {
                awkward.Add($"'{label}' begins with a digit");
            }

            if (label.Any(char.IsAsciiLetterUpper)) {
                awkward.Add($"'{label}' contains an uppercase letter");
            }
        }

        if (awkward.Count > 0) {
            context.Warn(
                "info.identifier.characters",
                $"identifier '{info.Identifier}': {string.Join("; ", awkward)}",
                "identities are compared ordinally and never case-folded — see BundlePermissions on "
                + "why nothing in a signed document is quietly corrected — so a capital letter here "
                + "makes a different application, with a different container, from the one a user "
                + "types",
                $"use lowercase letters, digits and hyphens only: \"{info.Identifier.ToLowerInvariant().Replace('_', '-')}\"",
                BundleLayout.InfoPath
            );
        }

        context.Ran("info.identifier.length");
        var unit = SandboxUnitBuilder.UnitNameFor(info.Identifier);
        if (unit.Length > MaximumUnitNameLength) {
            context.Error(
                "info.identifier.length",
                $"identifier '{info.Identifier}' makes a {unit.Length.ToString(CultureInfo.InvariantCulture)}-character unit name",
                "every launch creates a transient unit called "
                + $"'{SandboxUnitBuilder.UnitPrefix}<identifier>.service', and systemd refuses a unit "
                + $"name longer than {MaximumUnitNameLength.ToString(CultureInfo.InvariantCulture)} "
                + "characters — so the application would install and never start",
                "shorten the identifier to at most "
                + (MaximumUnitNameLength - SandboxUnitBuilder.UnitPrefix.Length - ".service".Length)
                    .ToString(CultureInfo.InvariantCulture)
                + " characters",
                BundleLayout.InfoPath
            );
        }
    }

    static void Name(DoctorContext context, BundleInfo info, string bundlePath) {
        context.Ran("info.name.present");
        if (string.IsNullOrWhiteSpace(info.Name)) {
            context.Error(
                "info.name.present",
                "name is empty",
                "it is what the dock, the installer and `systemctl status` call this application; "
                + "`trinix-bundle seal` refuses the bundle",
                "set \"name\" to the name a person should see",
                BundleLayout.InfoPath
            );

            return;
        }

        context.Ran("info.name.matches-directory");
        var directoryName = BundleLayout.NameOf(bundlePath);
        if (!string.Equals(directoryName, info.Name, StringComparison.Ordinal)) {
            context.Warn(
                "info.name.matches-directory",
                $"the bundle directory is '{directoryName}.app' but the name inside it is '{info.Name}'",
                "a user sees the directory name in Files and the declared name everywhere else, so "
                + "the two disagreeing means the application appears under two names on one machine",
                $"rename the bundle to '{info.Name}.app', or set \"name\": \"{directoryName}\"",
                BundleLayout.InfoPath
            );
        }
    }

    static void Version(DoctorContext context, BundleInfo info) {
        context.Ran("info.version.form");
        if (!BundleInfo.IsOrderedVersion(info.Version)) {
            context.Error(
                "info.version.form",
                $"version '{info.Version}' is not dotted-numeric",
                "\"version\" is the ordered one — what decides whether an update is an update — and it "
                + "is parsed as digits and dots and nothing else. `trinix-bundle seal` refuses the bundle",
                $"set \"version\" to digits and dots (\"{NumericPartOf(info.Version)}\") and put "
                + $"'{info.Version}' in \"shortVersion\", which is the one a person reads",
                BundleLayout.InfoPath
            );
        }

        // ⚠ Checked even when the ordered version is already refused. They are two
        // fields, and a developer fixing the first should not have to run doctor again
        // to be told about the second — which is the whole reason Validate() returns
        // every problem rather than the first one.
        context.Ran("info.version.short");
        if (info.ShortVersion is not null && string.IsNullOrWhiteSpace(info.ShortVersion)) {
            context.Warn(
                "info.version.short",
                "shortVersion is present and empty",
                "an empty display version is not the same as an absent one: anything showing it gets "
                + "a blank where the ordered version would otherwise have been shown",
                "remove \"shortVersion\", or give it a value",
                BundleLayout.InfoPath
            );
        }
    }

    static void Minimum(DoctorContext context, BundleInfo info, DoctorOptions options) {
        context.Ran("info.minimum.declared");

        if (info.MinimumSystemVersion is null) {
            context.Warn(
                "info.minimum.declared",
                "minimumSystemVersion is not set",
                "an absent minimum is a claim to run on every Trinix that will ever exist, including "
                + "the ones released before the APIs this application uses — and the failure lands on "
                + "a user as a crash rather than on the installer as a refusal",
                "set \"minimumSystemVersion\" to the oldest system you have actually run this on",
                BundleLayout.InfoPath
            );

            return;
        }

        var declared = info.MinimumSystemVersion;

        context.Ran("info.minimum.form");
        if (Truncates(declared, out var read)) {
            context.Warn(
                "info.minimum.form",
                read.Length == 0
                    ? $"minimumSystemVersion '{declared}' has no numeric component at all, so every "
                    + "system satisfies it"
                    : $"minimumSystemVersion '{declared}' is not dotted-numeric, and is compared as "
                    + $"'{read}'",
                read.Length == 0
                    ? "SystemVersion.Satisfies treats a version it cannot parse as 'yes' — which "
                    + "turns a minimum into no minimum, and does it silently. Doc 18 § R13 records "
                    + "the comparison as pinned rather than fixed, because it decides which "
                    + "applications may launch"
                    : "SystemVersion.Satisfies stops parsing at the first component that is not all "
                    + "digits, so a suffix silently discards everything after it — and it discards a "
                    + "different amount at different depths. Doc 18 § R13 records this as pinned "
                    + "rather than fixed, because it decides which applications may launch",
                $"use a dotted-numeric minimum ('{NumericPartOf(declared)}' if that is what you "
                + "meant). Until R13 is settled, a suffixed minimum means neither 'at least' nor "
                + "'before' — it means whatever survives the parse",
                BundleLayout.InfoPath
            );
        }

        var system = options.SystemVersion ?? SystemVersion.Current();
        if (system is null) {
            context.Skip(
                "info.minimum.system",
                $"no {SystemVersion.OsReleasePath} here, so there is no system version to compare "
                + "against — this check answers on a Trinix machine, or with an explicit one"
            );

            return;
        }

        context.Ran("info.minimum.system");

        if (Truncates(system, out var systemRead)) {
            context.Warn(
                "info.minimum.system",
                $"this system reports VERSION_ID '{system}', which is compared as "
                + (systemRead.Length == 0 ? "nothing at all" : $"'{systemRead}'"),
                "doc 18 § R13 again, from the other side: a prerelease system truncates to something "
                + "much older than it is, so on a '0.4-rc1' machine every application that names any "
                + "minimum at all is refused",
                "nothing in this bundle — the finding is about the machine doctor is running on, and "
                + "it is the case R13 says will bite first",
                BundleLayout.InfoPath
            );

            return;
        }

        if (!SystemVersion.Satisfies(system, declared)) {
            context.Note(
                "info.minimum.system",
                $"this bundle asks for at least '{declared}' and this system is '{system}'",
                "it will be refused here, correctly — reported so a build machine older than the "
                + "target is not mistaken for a broken bundle",
                where: BundleLayout.InfoPath
            );
        }
    }

    /// <summary>
    ///     Does <see cref="SystemVersion" /> read fewer components out of this than it
    ///     contains?
    /// </summary>
    /// <param name="value">A version string.</param>
    /// <param name="read">
    ///     What the comparison will actually see — the empty string when nothing parses
    ///     at all, which <see cref="SystemVersion.Satisfies" /> treats as "yes to
    ///     everything" rather than as zero.
    /// </param>
    /// <remarks>
    ///     ⚠ This mirrors <c>SystemVersion.Parse</c>, which is private, and it is the
    ///     one predicate in this assembly that could drift from the code it describes.
    ///     Mirrored rather than exposed because R13 is an open decision: making the
    ///     parse public would freeze the behaviour doc 18 wants changed. The agreement
    ///     is pinned instead — <c>WhatDoctorSaysAVersionIsComparedAsIsWhatTheComparisonDoes</c>
    ///     asserts that <paramref name="read" /> and <paramref name="value" /> satisfy
    ///     exactly the same minimums, so a fix to R13 shows up as a failing test rather
    ///     than as a warning that has quietly become untrue.
    /// </remarks>
    internal static bool Truncates(string value, out string read) {
        List<string> kept = [];
        var parts = value.Split('.');

        foreach (var part in parts) {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)) {
                break;
            }

            kept.Add(part);
        }

        read = string.Join('.', kept);
        return kept.Count != parts.Length;
    }

    /// <summary>
    ///     The version somebody probably meant, for a suggested fix.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately <b>not</b> <see cref="Truncates" />'s answer. That one says
    ///     what the comparison sees — <c>1.0.0-beta</c> is compared as <c>1.0</c> — and
    ///     offering <c>1.0</c> as the replacement for <c>1.0.0-beta</c> would suggest an
    ///     edit that loses a component the author clearly meant. This strips the suffix
    ///     from each component instead, which is what a person would have typed.
    /// </remarks>
    static string NumericPartOf(string value) {
        List<string> parts = [];
        foreach (var part in value.Split('.')) {
            var digits = new string([.. part.TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length == 0) {
                break;
            }

            parts.Add(digits);
        }

        return parts.Count == 0 ? "1.0.0" : string.Join('.', parts);
    }

    /// <summary>A plausible last label, for a suggested identifier.</summary>
    static string Slug(string name) {
        var slug = new string([.. name.Where(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
        return slug.Length == 0 ? "app" : slug;
    }
}

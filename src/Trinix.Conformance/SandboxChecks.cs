using System.Globalization;
using Trinix.Bundle;
using Trinix.Sandbox;

namespace Trinix.Conformance;

/// <summary>
///     What the sandbox would actually do to this bundle.
/// </summary>
/// <remarks>
///     <para>
///         The reason doctor is worth building. Every other check in this assembly
///         answers a question a developer could have answered by reading the format
///         carefully enough; this one answers a question nobody can answer by reading
///         anything: <i>which of the permissions I declared are enforced by nothing on
///         the system this ships to?</i> The answer is
///         <see cref="SandboxUnit.Gaps" />, and it exists because doc 04 asked that "a
///         permission enforced by nothing should be a line in the journal rather than a
///         silence" — the same sentence, one step earlier, is a line in a conformance
///         report rather than a surprise after shipping.
///     </para>
///     <para>
///         ⚠ <b>Every gap is a note, including the dangerous one.</b> Ten of the
///         fourteen permissions are the broker's and the broker does not exist yet;
///         reporting that as a defect would be blaming a developer for the state of the
///         system, and a report where two thirds of the lines are unactionable warnings
///         is a report nobody reads to the end. <see cref="SandboxGapKind.Inert" /> was
///         a warning for about an hour and should not have been: it fires on every
///         bundle on the current image, identically, so it measures the system and not
///         the thing under test — and a severity that is always on is a severity that
///         gets filtered out, taking the warnings that <i>are</i> about the bundle with
///         it. It keeps its own check id and a ⚠ in its text instead, because it is
///         still the gap a reader gets wrong by looking.
///     </para>
///     <para>
///         ⚠ The layout handed to the builder is a plausible one, not this machine's.
///         Doctor runs where bundles are built — which is not Trinix — so asking
///         <see cref="SandboxLayout.ForCurrentUser" /> would fail on a Mac and would
///         answer about the wrong machine everywhere else. The unit's <i>properties</i>
///         depend on the paths; its <i>gaps</i> do not, and the gaps are what this
///         section reports.
///     </para>
/// </remarks>
static class SandboxChecks {
    /// <summary>The user a hypothetical Trinix machine runs the application as.</summary>
    /// <remarks>
    ///     Named here rather than discovered, and never used for anything but building a
    ///     layout that validates. See the type's remarks.
    /// </remarks>
    internal const string PlausibleUser = "trinix";

    /// <summary>Their uid, for <c>XDG_RUNTIME_DIR</c> and the Wayland socket's path.</summary>
    internal const uint PlausibleUserId = 1000;

    internal static void Run(
        DoctorContext context,
        BundleInfo info,
        string bundlePath,
        IReadOnlyList<BundleEntry> entries,
        DoctorOptions options
    ) {
        context.Ran("sandbox.unit");

        var capabilities = Capabilities(context, options);

        var layout = SandboxLayout.For(
            info,
            "/Applications/" + Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundlePath))),
            PlausibleUser,
            PlausibleUserId,
            "/home/" + PlausibleUser
        );

        var runtime = BundleRuntimeDetection.DetectFrom(
            entries.Where(entry => entry.Kind == EntryKind.File).Select(entry => entry.RelativePath)
        );

        SandboxUnit unit;
        try {
            unit = SandboxUnitBuilder.Build(info, layout, new SandboxOptions {
                Capabilities = capabilities,
                Runtime = runtime
            });
        } catch (BundleException e) {
            context.Error(
                "sandbox.unit",
                "no sandbox can be built for this bundle: " + e.Message,
                "the launcher builds the transient unit from the signed manifest before it starts "
                + "anything, so a bundle this refuses is one that installs and never runs",
                "fix the findings above; every input to the unit comes from " + BundleLayout.InfoPath,
                BundleLayout.InfoPath
            );

            return;
        }

        Containment(context, unit);
        Gaps(context, unit);
    }

    /// <summary>
    ///     Which systemd the gaps below are about, said out loud every time.
    /// </summary>
    /// <remarks>
    ///     ⚠ The note is unconditional, including when the caller supplied the answer.
    ///     Every line in this section is "on the system this ships to, X is not
    ///     enforced", and a reader cannot evaluate any of them without knowing which
    ///     system was meant — a report that silently answered about the build host
    ///     would be wrong in the reassuring direction.
    /// </remarks>
    static SandboxCapabilities Capabilities(DoctorContext context, DoctorOptions options) {
        context.Ran("sandbox.systemd");

        if (options.Capabilities is { } supplied) {
            context.Note(
                "sandbox.systemd",
                "the capability set was supplied by the caller (seccomp: "
                + (supplied.Seccomp ? "yes" : "no") + ")",
                "the gaps below describe that system rather than this machine or the Trinix image"
            );

            return supplied;
        }

        var probed = SystemdProbe.Capabilities(out var problem);
        if (problem is null) {
            context.Note(
                "sandbox.systemd",
                "asked this machine's systemd what it supports"
                + (probed.Features is { } features ? ": systemd " + features.VersionText : string.Empty),
                "the gaps below are this machine's, which is only the right answer if this machine "
                + "is the one the application will run on"
            );

            return probed;
        }

        context.Note(
            "sandbox.systemd",
            "no systemd here (" + problem + "), so the gaps below are the Trinix image's",
            "SandboxCapabilities.TrinixToday is what base/recipes/systemd builds — no libseccomp, "
            + "which decides five of doc 04's properties — and it is pinned against the feature "
            + "string captured from the image, so it is a measurement rather than a guess",
            "nothing: doctor runs on the machine that builds a bundle, and answers about the one "
            + "that runs it"
        );

        return SandboxCapabilities.TrinixToday;
    }

    static void Containment(DoctorContext context, SandboxUnit unit) {
        context.Ran("sandbox.network");

        var network = unit.Enforces("PrivateNetwork")
            ? "no network stack at all (PrivateNetwork=yes)"
            : "the shared application network namespace (" + (unit.ValueOf("NetworkNamespacePath") ?? "?") + ")";

        context.Note(
            "sandbox.network",
            "this application will run with " + network,
            "the one containment property on the image that was proven rather than assumed: "
            + "`ip link show eth0` fails inside a unit that sets it and succeeds in the control"
        );

        context.Ran("sandbox.runtime");
        if (unit.Runtime == BundleRuntime.Unknown) {
            context.Note(
                "sandbox.runtime",
                "nothing in the bundle says what kind of code the entry point is",
                "so MemoryDenyWriteExecute= is left off: Info.json cannot declare NativeAOT, and "
                + "inferring it from an absence would crash a JIT at its first compiled method "
                + "rather than at startup",
                "nothing yet — this needs a field the bundle format does not have"
            );
        }
    }

    static void Gaps(DoctorContext context, SandboxUnit unit) {
        context.Ran("sandbox.gaps");
        context.Ran("sandbox.inert");

        foreach (var gap in unit.Gaps) {
            if (gap.Kind == SandboxGapKind.Inert) {
                context.Note(
                    "sandbox.inert",
                    $"⚠ {gap.Subject} is set on the unit and enforces nothing",
                    gap.Reason,
                    "nothing in this bundle, and it is the same on every bundle — which is exactly "
                    + "why it is a note and not a warning: a severity that fires identically for "
                    + "everyone is measuring the system rather than the thing under test. ⚠ It is "
                    + "still the gap a reader gets wrong by looking, because `systemctl show` "
                    + "reports it byte-for-byte as it would on a machine that enforces it"
                );

                continue;
            }

            context.Note(
                "sandbox.gaps",
                $"{gap.Subject}: not enforced by the unit ({Kind(gap.Kind)})",
                gap.Reason,
                Advice(gap.Kind)
            );
        }

        context.Ran("sandbox.enforced");
        var enforced = unit.Properties.Count(p => p.Enforcement == PropertyEnforcement.Enforced);
        context.Note(
            "sandbox.enforced",
            enforced.ToString(CultureInfo.InvariantCulture) + " unit properties will be enforced, "
            + unit.InertProperties.Count.ToString(CultureInfo.InvariantCulture) + " will not, and "
            + unit.Gaps.Count.ToString(CultureInfo.InvariantCulture) + " things doc 04 asks for are "
            + "left to something that is not the unit",
            "the containment floor is unconditional and permissions only ever add to it, so the "
            + "worst case of a misread manifest is an application more contained than intended"
        );
    }

    static string Kind(SandboxGapKind kind) =>
        kind switch {
            SandboxGapKind.Broker => "the broker decides",
            SandboxGapKind.Compositor => "the compositor decides",
            SandboxGapKind.SessionManager => "the session manager decides",
            SandboxGapKind.SystemCapability => "this systemd cannot be asked",
            SandboxGapKind.Inert => "accepted and inert",
            SandboxGapKind.NoSuchProperty => "systemd has no property for it",
            SandboxGapKind.Runtime => "the runtime cannot tolerate it",
            SandboxGapKind.BundleFormat => "Info.json cannot say",
            _ => kind.ToString()
        };

    static string? Advice(SandboxGapKind kind) =>
        kind switch {
            SandboxGapKind.Broker =>
                "nothing in this bundle: declaring the permission buys the right to ask, and the "
                + "thing that asks the user and says no is trinix-broker, which is not written yet. "
                + "Design for the request being refused",
            SandboxGapKind.Compositor =>
                "nothing in this bundle: a bound Wayland socket is not a granted window",
            SandboxGapKind.SessionManager =>
                "nothing in this bundle, and do not rely on the process outliving its last window "
                + "until the session manager says it may",
            SandboxGapKind.BundleFormat =>
                "nothing yet — the fact the enforcement needs is one Info.json cannot express, and "
                + "the gap is recorded so the request for a schema field is visible rather than "
                + "living in somebody's head",
            _ => "nothing in this bundle — it is a property of the system it will run on"
        };
}

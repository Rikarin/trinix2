using System.Diagnostics;
using System.Globalization;
using Trinix.Bundle;
using Trinix.Sandbox;

namespace Trinix.Gatekeeper;

/// <summary>
///     Launching a verified bundle inside a transient systemd unit.
/// </summary>
/// <remarks>
///     <para>
///         Doc 04's central claim, wired up: "Launching an application becomes: verify
///         the bundle (already built), <b>construct a transient systemd unit</b> from
///         its signed permissions, and start it there."
///     </para>
///     <para>
///         ⚠ <b>A separate branch, and <c>Launcher.Spawn</c>/<c>Launcher.Exec</c> are
///         untouched.</b> That path is what <c>open --wait</c> is, what the unit files
///         use, and what the Phase 6 checks drive; it works today on a machine with no
///         composed root and no privileged path to the system manager, which is every
///         machine there is. Replacing it with this one would trade a launcher that
///         works for a launcher that is correct in a world that does not exist yet. So
///         the sandbox is opt-in behind <c>--sandbox</c> until the three prerequisites
///         <see cref="SandboxPreflight" /> names have been built, and then the default
///         flips in a commit that changes one branch rather than a design.
///     </para>
///     <para>
///         ⚠ <b>Nothing in this file has ever run.</b> There is no systemd on the
///         machine it was written on, the composed root it mounts does not exist in any
///         image, and no test here starts a process. What <i>is</i> tested is everything
///         it hands off to: the unit, the gaps, the probe's parsing and the preflight's
///         verdicts. The residue — that <c>systemd-run</c> is invoked correctly, that it
///         accepts these spellings, that the resulting container can execute a .NET
///         application — is unverified, and <c>--print-unit</c> exists so that the first
///         person with a booted VM can read what would be sent before sending it.
///     </para>
/// </remarks>
static class Sandboxed {
    /// <summary>How long <c>systemd-run</c> gets to create the unit.</summary>
    /// <remarks>
    ///     ⚠ <c>systemd-run</c> is not the application: it makes one bus call and exits,
    ///     and the application it started outlives it as a child of PID 1. Waiting for
    ///     it is therefore not the mistake <c>Launcher</c>'s remarks warn about — it is
    ///     the only way to see the D-Bus error when a property is refused, which on this
    ///     systemd is a thing that measurably happens.
    /// </remarks>
    const int CreateTimeoutMilliseconds = 30_000;

    /// <summary>
    ///     Build the unit for a verified bundle and either print it or start it.
    /// </summary>
    /// <param name="bundlePath">The verified bundle, on the host.</param>
    /// <param name="result">What <c>BundleVerifier</c> returned. Must have succeeded.</param>
    /// <param name="applicationArguments">Arguments for the application itself.</param>
    /// <param name="printOnly">
    ///     Print the unit and its gaps and stop. ⚠ Deliberately does every step that does
    ///     not touch the machine — including the probe, so that what is printed is what
    ///     <i>this</i> systemd would be sent — and none that does.
    /// </param>
    /// <returns>A value from <see cref="ExitCode" />.</returns>
    internal static int Launch(
        string bundlePath,
        VerificationResult result,
        IReadOnlyList<string> applicationArguments,
        bool printOnly
    ) {
        var info = result.Info!;

        if (!TryTargetUser(out var user, out var userProblem)) {
            Console.Error.WriteLine($"open: cannot sandbox {info.Identifier}");
            Console.Error.WriteLine("  " + userProblem);
            return ExitCode.Failed;
        }

        var layout = SandboxLayout.For(info, bundlePath, user);

        // The runtime kind decides exactly one property, MemoryDenyWriteExecute=, and
        // it is read out of the *signed* manifest rather than a directory listing: a
        // conclusion drawn from the manifest is as trustworthy as the signature.
        var runtime = result.Manifest is null
            ? BundleRuntime.Unknown
            : BundleRuntimeDetection.DetectFrom(result.Manifest);

        // ⚠ The probe, not a constant. What the syscall floor costs is decided by the
        // binary that is about to be asked for it, and `systemctl --version` is the
        // only source that knows — `systemctl show -p SystemCallFilter` answers with a
        // value for a property this systemd will refuse to set.
        var capabilities = SystemdProbe.Capabilities(out var probeProblem);
        if (probeProblem is not null) {
            Console.Error.WriteLine($"open: could not read systemd's feature string ({probeProblem});");
            Console.Error.WriteLine("  assuming the weakest capabilities, which is the safe direction.");
        }

        SandboxUnit unit;
        try {
            unit = SandboxUnitBuilder.Build(info, layout, new SandboxOptions {
                Capabilities = capabilities,
                Runtime = runtime,
                Arguments = applicationArguments
            });
        } catch (BundleException e) {
            Console.Error.WriteLine($"open: cannot sandbox {Path.GetFileName(bundlePath)}");
            Console.Error.WriteLine("  " + e.Message);
            return e.Failure == BundleFailure.UnknownPermission ? ExitCode.Refused : ExitCode.Failed;
        }

        Journal(unit, capabilities);

        if (printOnly) {
            Console.WriteLine();
            Console.WriteLine(unit.ToUnitFile());
            return ExitCode.Ok;
        }

        // The one prerequisite this program owns. Everything else it can only name.
        if (!SandboxPreflight.TryPrepareContainer(layout, user.GroupId, out var containerProblem)) {
            Console.Error.WriteLine($"open: cannot sandbox {Path.GetFileName(bundlePath)}");
            Console.Error.WriteLine("  " + containerProblem);
            return ExitCode.Failed;
        }

        var missing = SandboxPreflight.Check(layout, unit.Permissions);
        if (missing.Count > 0) {
            // ⚠ Named, one per line, with an owner. The alternative is emitting the
            // unit anyway and letting systemd fail on a mount source that does not
            // exist — which produces an error about a namespace, several layers below
            // the thing that is actually wrong, on a machine where the honest answer is
            // "nothing has ever created that directory".
            Console.Error.WriteLine($"open: cannot sandbox {Path.GetFileName(bundlePath)}");
            foreach (var prerequisite in missing) {
                Console.Error.WriteLine($"  {prerequisite.Subject}: {prerequisite.Problem}");
                Console.Error.WriteLine($"    owed by: {Owner(prerequisite.Owner)}");
            }

            Console.Error.WriteLine("  Run `open` without --sandbox to launch it uncontained, as before.");
            return ExitCode.Failed;
        }

        return Start(unit, info);
    }

    /// <summary>
    ///     Put everything the unit does not enforce into the journal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Doc 04, on audit mode: "a permission enforced by nothing should be a line
    ///         in the journal rather than a silence." As of the 2026-08-25 measurement
    ///         that sentence is true of three <i>properties</i> as well as of ten
    ///         permissions — <c>MemoryDenyWriteExecute=</c>, <c>RestrictRealtime=</c> and
    ///         <c>RestrictSUIDSGID=</c> are accepted by this systemd, are reported back
    ///         by <c>systemctl show</c> exactly as they would be on a machine that
    ///         enforces them, and enforce nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ Stdout, in the same machine-readable shape as <c>BUNDLE-VERIFIED</c> and
    ///         <c>BUNDLE-LAUNCHED</c>, because under systemd stdout <i>is</i> the journal
    ///         and because <c>trinix doctor</c> will want to read these back. The inert
    ///         ones are additionally counted onto stderr: a person watching a terminal
    ///         should not have to read fourteen lines to notice that the hardening they
    ///         can see in <c>systemctl show</c> is not real.
    ///     </para>
    /// </remarks>
    static void Journal(SandboxUnit unit, SandboxCapabilities capabilities) {
        foreach (var gap in unit.Gaps) {
            var mark = gap.Kind == SandboxGapKind.Inert ? "⚠ " : string.Empty;
            Console.WriteLine($"SANDBOX-GAP {gap.Kind} {gap.Subject} — {mark}{gap.Reason}");
        }

        var inert = unit.InertProperties;
        if (inert.Count == 0) {
            return;
        }

        var evidence = capabilities.Features is { VersionText.Length: > 0 } features
            ? $"{features.VersionText} reports -{SystemdFeatures.Seccomp}"
            : "systemd's feature string could not be read, so the weakest assumption was made";

        var count = inert.Count.ToString(CultureInfo.InvariantCulture);
        var names = string.Join(", ", inert.Select(p => p.Name));

        Console.Error.WriteLine(
            $"open: ⚠ {count} of this unit's properties are set and enforce nothing ({names}). {evidence}."
        );
    }

    /// <summary>
    ///     Hand the unit to <c>systemd-run</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>Process</c> rather than <c>Launcher.Spawn</c>, and the reason is the
    ///     inverse of the one in <c>Launcher</c>'s own remarks. There, a .NET parent
    ///     between the session and the application would have to forward signals, exit
    ///     codes and a terminal; here the application's parent is PID 1 and this process
    ///     is the parent of nothing but <c>systemd-run</c>, which exits as soon as the
    ///     unit exists. Waiting for it is what turns a refused property from silence
    ///     into a message naming the property.
    /// </remarks>
    static int Start(SandboxUnit unit, BundleInfo info) {
        var arguments = unit.ToSystemdRunArguments();

        var start = new ProcessStartInfo(SystemdProbe.SystemdRun) {
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(start);
            if (process is null) {
                Console.Error.WriteLine($"open: could not start {SystemdProbe.SystemdRun}");
                return ExitCode.Failed;
            }

            var diagnostics = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(CreateTimeoutMilliseconds)) {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }

                Console.Error.WriteLine($"open: systemd-run did not answer while creating {unit.UnitName}");
                return ExitCode.Failed;
            }

            if (process.ExitCode != 0) {
                // ⚠ The failure this slice was blocked on, and the one whose text
                // matters most: a property systemd will not take is reported here by
                // name, e.g. "Cannot set property SystemCallFilter, or unknown
                // property". Swallowing it would leave a launcher that refuses without
                // saying which of forty properties was the problem.
                Console.Error.WriteLine($"open: systemd refused to create {unit.UnitName}");
                foreach (var line in diagnostics.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
                    Console.Error.WriteLine("  " + line);
                }

                return ExitCode.Failed;
            }
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) {
            Console.Error.WriteLine($"open: could not run {SystemdProbe.SystemdRun}: {e.Message}");
            return ExitCode.Failed;
        }

        Console.WriteLine($"BUNDLE-SANDBOXED {info.Identifier} {info.Version} unit {unit.UnitName}");
        return ExitCode.Ok;
    }

    /// <summary>
    ///     Whose application this is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The awkward consequence of doc 04's unplanned privilege path.</b> A
    ///         <c>RootDirectory=</c> unit has to be created on the system manager, so
    ///         <c>open --sandbox</c> has to be privileged — and a privileged process's
    ///         own uid is root, which is emphatically not who the application belongs
    ///         to. Running it as root would give it root's <c>$HOME</c>, root's
    ///         container, and root's uid inside a container built to contain a user's
    ///         application.
    ///     </para>
    ///     <para>
    ///         ⚠ So when this process is root it takes the target user from
    ///         <c>SUDO_UID</c>, and that is a stopgap wearing a label rather than a
    ///         design. It trusts an environment variable for a security-relevant
    ///         identity — bounded, because it is only consulted once the process is
    ///         already root, so whoever set it already had every privilege it could
    ///         confer, and the worst it can do is give an application to the wrong
    ///         unprivileged user rather than grant anyone anything. The real answer is
    ///         doc 04's: a polkit-mediated or <c>trinixd</c>-mediated verb that carries
    ///         the caller's identity as part of the request instead of in its
    ///         environment.
    ///     </para>
    /// </remarks>
    static bool TryTargetUser(out SandboxUser user, out string problem) {
        user = null!;

        if (!SandboxUser.TryCurrent(out var current, out var resolveProblem)) {
            problem = resolveProblem ?? "the current user could not be resolved";
            return false;
        }

        if (current.UserId != 0) {
            user = current;
            problem = string.Empty;
            return true;
        }

        var sudo = Environment.GetEnvironmentVariable("SUDO_UID");
        if (!uint.TryParse(sudo, CultureInfo.InvariantCulture, out var userId) || userId == 0) {
            problem = "this process is root and nothing says whose application this is. A "
                + "RootDirectory= unit needs the system manager, so the launch has to be "
                + "privileged, but running the application as root would give it root's home "
                + "and root's uid. Invoke it through sudo (which sets SUDO_UID), or wait for "
                + "doc 04's privileged path to the system manager, which carries the caller's "
                + "identity properly";

            return false;
        }

        if (!SandboxUser.TryResolve(userId, out var target, out resolveProblem)) {
            problem = $"SUDO_UID says {userId} and " + (resolveProblem ?? "that user could not be resolved");
            return false;
        }

        user = target;
        problem = string.Empty;
        return true;
    }

    /// <summary>One phrase per owner, for a refusal a person can act on.</summary>
    static string Owner(SandboxPrerequisiteOwner owner) =>
        owner switch {
            SandboxPrerequisiteOwner.Launcher => "open itself — which means this is a bug here, not a missing piece",
            SandboxPrerequisiteOwner.SystemImage => "base/ and image/: it has to be in the signed system image",
            SandboxPrerequisiteOwner.Boot => "something that runs at boot, before the first application that needs it",
            SandboxPrerequisiteOwner.Unplanned => "nobody yet — doc 04 lists it as a prerequisite it did not budget",
            _ => "unknown"
        };
}

using System.Diagnostics;

namespace Trinix.Sandbox;

/// <summary>
///     Asking the systemd that is about to run the unit what it was built with.
/// </summary>
/// <remarks>
///     <para>
///         The whole of this type is "run a binary and hand its stdout to
///         <see cref="SystemdFeatures.TryParse" />". Everything with a decision in it
///         lives on the other side of that call, which is what lets the interesting
///         behaviour — this banner means the syscall floor is off — be tested against a
///         captured string rather than requiring a booted machine.
///     </para>
///     <para>
///         ⚠ <b>Absolute paths, never <c>PATH</c>.</b> The answer decides how contained
///         an application is, so a probe that resolved <c>systemctl</c> through the
///         environment would let whoever set that environment choose the answer — and
///         the answer they would choose is <c>+SECCOMP</c>, which produces a launch that
///         fails and, if the failure were ever tolerated, three properties recorded as
///         enforcing while enforcing nothing.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is exercised by a test.</b> There is no systemd on the
///         machine this is developed on, and the CI job is a <c>dotnet test</c> on
///         macOS. What the tests cover is every branch after the process exits; the
///         process itself is unverified until it runs on a booted Trinix.
///     </para>
/// </remarks>
public static class SystemdProbe {
    /// <summary>The binaries that will print a version banner, in order of preference.</summary>
    /// <remarks>
    ///     <c>systemctl</c> first because it is on every systemd system by definition,
    ///     and <c>systemd-analyze</c> second because it prints the identical banner and
    ///     is occasionally the one that survives a minimal install. Both are asked for
    ///     the same string; neither is asked for anything else.
    /// </remarks>
    public static IReadOnlyList<string> VersionCommands { get; } = [
        "/usr/bin/systemctl",
        "/bin/systemctl",
        "/usr/bin/systemd-analyze"
    ];

    /// <summary>The binary that creates a transient unit.</summary>
    /// <remarks>
    ///     ⚠ Named here rather than in the launcher because the launcher should not be
    ///     the place that knows where systemd lives. Trinix is a merged-<c>/usr</c>
    ///     system, so this is the only path that exists on the image.
    /// </remarks>
    public const string SystemdRun = "/usr/bin/systemd-run";

    /// <summary>How long a version banner is allowed to take.</summary>
    /// <remarks>
    ///     ⚠ A timeout at all, because this is on the launch path. <c>systemctl
    ///     --version</c> does not talk to the bus and should answer in milliseconds; a
    ///     probe that hung would turn "double-click an icon" into "nothing happens
    ///     forever", which is a worse outcome than launching with the pessimistic
    ///     capability set.
    /// </remarks>
    public const int TimeoutMilliseconds = 5_000;

    /// <summary>
    ///     Read the feature string off this machine's systemd.
    /// </summary>
    /// <param name="features">What it reported, when one of the binaries answered.</param>
    /// <param name="problem">One line saying why not, otherwise.</param>
    /// <returns><see langword="true" /> when <paramref name="features" /> is set.</returns>
    public static bool TryRead(out SystemdFeatures features, out string? problem) {
        features = SystemdFeatures.Unknown;
        problem = "no systemd on this machine: none of " + string.Join(", ", VersionCommands) + " exists";

        foreach (var command in VersionCommands) {
            if (!File.Exists(command)) {
                continue;
            }

            if (!TryRun(command, out var banner, out problem)) {
                // Keep going: a systemctl that exists and will not run is a reason to
                // try systemd-analyze, not a reason to stop. The last problem wins,
                // which is the one a person can act on.
                continue;
            }

            if (SystemdFeatures.TryParse(banner, out var parsed, out problem)) {
                features = parsed;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     What this machine's systemd can be asked for.
    /// </summary>
    /// <param name="problem">
    ///     <see langword="null" /> when the probe succeeded, and otherwise one line
    ///     naming what could not be read — for a journal line, not for a refusal.
    /// </param>
    /// <remarks>
    ///     ⚠ A failed probe is not a refusal to launch, and that asymmetry is chosen
    ///     rather than convenient. An unreadable banner means the pessimistic capability
    ///     set: the two properties that would be refused are withheld, and the three
    ///     that would be inert are recorded as inert. That produces a unit which starts
    ///     on any systemd and overstates nothing. Refusing instead would mean a machine
    ///     with an odd <c>systemctl</c> cannot run applications at all, which is a large
    ///     penalty for the failure of a probe whose worst case is already safe.
    /// </remarks>
    public static SandboxCapabilities Capabilities(out string? problem) =>
        TryRead(out var features, out problem)
            ? SandboxCapabilities.From(features)
            : SandboxCapabilities.From(SystemdFeatures.Unknown);

    /// <summary>Run one binary with <c>--version</c> and collect its stdout.</summary>
    static bool TryRun(string command, out string banner, out string? problem) {
        banner = string.Empty;

        try {
            using var process = Process.Start(
                new ProcessStartInfo(command, "--version") {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            );

            if (process is null) {
                problem = command + " could not be started";
                return false;
            }

            // ⚠ Read before waiting. A process whose pipe fills while nobody drains it
            // blocks in write() and is then killed for taking too long, which would
            // read as "systemd is broken" rather than as "the launcher deadlocked it".
            // The banner is two lines, so this can never actually happen — but the
            // ordering costs nothing and the bug it prevents is untraceable.
            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(TimeoutMilliseconds)) {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }

                problem = command + " --version did not answer within "
                    + (TimeoutMilliseconds / 1000) + " seconds";

                return false;
            }

            if (process.ExitCode != 0) {
                problem = command + " --version exited " + process.ExitCode;
                return false;
            }

            banner = output;
            problem = null;
            return true;
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) {
            problem = "could not run " + command + " --version: " + e.Message;
            return false;
        }
    }
}

using System.ComponentModel;
using System.Management.Automation;

namespace Trinix.Management;

/// <summary>
///     Checks that the platform underneath this session is intact.
/// </summary>
/// <remarks>
///     <para>
///         Every check here answers a question that has a right answer on a healthy
///         Trinix system and a wrong one on a broken image: is the shell PowerShell,
///         does .NET run, did the C# service start, did the C# compositor start, is
///         the root filesystem read-only as the A/B model requires, is <c>/data</c>
///         mounted and writable.
///     </para>
///     <para>
///         It exists because Phase 7 needs it. An A/B update commits to a new slot
///         only if the system it booted works, and "works" has to mean something a
///         machine can evaluate — this is that definition, written once, used by the
///         updater, by the unattended boot check, and by anyone who wants to know
///         whether the box is healthy.
///     </para>
/// </remarks>
/// <example>
///     <code>Test-TrinixSystem</code>
/// </example>
/// <example>
///     <code>if (-not (Test-TrinixSystem -Quiet)) { "this slot is bad" }</code>
/// </example>
[Cmdlet(VerbsDiagnostic.Test, "TrinixSystem")]
[OutputType(typeof(TrinixCheck), typeof(bool))]
public sealed class TestTrinixSystemCommand : PSCmdlet {
    /// <summary>
    ///     Return a single boolean instead of one object per check.
    /// </summary>
    /// <remarks>
    ///     Failing checks are still written to the error stream, because a bare
    ///     <c>False</c> tells you that something is wrong and nothing about what.
    /// </remarks>
    [Parameter]
    public SwitchParameter Quiet { get; set; }

    static TrinixCheck CheckPowerShell() {
        var version = typeof(PSCmdlet).Assembly.GetName().Version;

        return new() {
            Component = "PowerShell", Ok = version is { Major: >= 7 }, Detail = version?.ToString() ?? "unknown"
        };
    }

    static TrinixCheck CheckDotNet() {
        // Runs the SDK rather than reporting the runtime this process happens
        // to be hosted by: pwsh carries its own copy, so asking it what it is
        // running on would pass on a system with no .NET installed at all.
        try {
            var result = Cli.Run("dotnet", "--version");
            var version = result.StandardOutput.Trim();

            return new() {
                Component = "DotNet",
                Ok = result.ExitCode == 0 && version.Length > 0,
                Detail = result.ExitCode == 0 ? version : result.StandardError.Trim()
            };
        } catch (Exception e) when (e is InvalidOperationException or Win32Exception) {
            return new() { Component = "DotNet", Ok = false, Detail = e.Message };
        }
    }

    // The compositor is on this list for the same reason the daemon is: an
    // update that boots into a system with no display server has not worked,
    // whatever else survived. Booting deliberately without one —
    // systemd.unit=multi-user.target — is a choice made on the kernel command
    // line, and a self-test that disagreed with it would be reporting on the
    // wrong question.
    static TrinixCheck CheckService(string component, string name) {
        try {
            var properties = Cli.ParseProperties(
                Cli.RunChecked("systemctl", "show", $"{name}.service", "--property=ActiveState,SubState")
            );
            var state = properties.GetValueOrDefault("ActiveState", "unknown");

            return new() {
                Component = component,
                Ok = state == "active",
                Detail = $"{name} is {state}/{properties.GetValueOrDefault("SubState", "unknown")}"
            };
        } catch (InvalidOperationException e) {
            return new() { Component = component, Ok = false, Detail = e.Message };
        }
    }

    static TrinixCheck CheckImmutableRoot() => MountCheck("ImmutableRoot", "/", true);

    static TrinixCheck CheckWritableData() => MountCheck("WritableData", "/data", false);

    static TrinixCheck MountCheck(string component, string mountPoint, bool requireReadOnly) {
        foreach (var line in Cli.Lines(Cli.ReadFileOrDefault("/proc/self/mounts", string.Empty))) {
            var fields = line.Split(' ');
            if (fields.Length < 4 || fields[1] != mountPoint) {
                continue;
            }

            var readOnly = fields[3].Split(',').Contains("ro");

            return new() {
                Component = component,
                Ok = readOnly == requireReadOnly,
                Detail = $"{fields[0]} {(readOnly ? "ro" : "rw")}"
            };
        }

        return new() { Component = component, Ok = false, Detail = $"{mountPoint} is not mounted" };
    }

    /// <summary>Runs every check.</summary>
    protected override void EndProcessing() {
        var checks = new List<TrinixCheck> {
            CheckPowerShell(),
            CheckDotNet(),
            CheckService("Daemon", "trinixd"),
            CheckService("Compositor", "trinix-compositor"),
            CheckImmutableRoot(),
            CheckWritableData()
        };

        if (!Quiet) {
            foreach (var check in checks) {
                WriteObject(check);
            }

            return;
        }

        foreach (var failure in checks.Where(c => !c.Ok)) {
            WriteError(
                new ErrorRecord(
                    new InvalidOperationException($"{failure.Component}: {failure.Detail}"),
                    "CheckFailed",
                    ErrorCategory.InvalidResult,
                    failure.Component
                )
            );
        }

        WriteObject(checks.TrueForAll(c => c.Ok));
    }
}

/// <summary>
///     The result of one platform check.
/// </summary>
public sealed class TrinixCheck {
    /// <summary>What was checked.</summary>
    public required string Component { get; init; }

    /// <summary>Whether it is in the state a healthy system would be in.</summary>
    public required bool Ok { get; init; }

    /// <summary>What was actually found, whether or not that was the right answer.</summary>
    public required string Detail { get; init; }
}

using System.Management.Automation;

namespace Trinix.Management;

/// <summary>
///     Lists system services and what they are doing.
/// </summary>
/// <example>
///     <code>Get-TrinixService trinixd</code>
/// </example>
/// <example>
///     <code>Get-TrinixService | Where-Object { -not $_.IsHealthy }</code>
/// </example>
[Cmdlet(VerbsCommon.Get, "TrinixService")]
[OutputType(typeof(TrinixService))]
public sealed class GetTrinixServiceCommand : PSCmdlet {
    /// <summary>
    ///     Service names to report on. Accepts systemd's glob patterns.
    /// </summary>
    /// <remarks>
    ///     Omit to list every loaded service. The <c>.service</c> suffix is
    ///     optional — typing it is systemd's habit, not a user's.
    /// </remarks>
    [Parameter(Position = 0, ValueFromPipeline = true)]
    public string[]? Name { get; set; }

    /// <summary>Report only services that are not running as intended.</summary>
    [Parameter]
    public SwitchParameter Failed { get; set; }

    IEnumerable<string> ResolveUnitNames() {
        if (Name is { Length: > 0 }) {
            return Name.Select(n => n.Contains('.', StringComparison.Ordinal) ? n : n + ".service");
        }

        // --plain drops the tree-drawing characters, --no-legend the trailing
        // prose. What is left is one unit per line, name first.
        var listed = Cli.RunChecked(
            "systemctl",
            "list-units",
            "--type=service",
            "--all",
            "--plain",
            "--no-legend",
            "--no-pager"
        );

        return Cli.Lines(listed)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!);
    }

    /// <summary>Queries systemd and writes one object per service.</summary>
    protected override void EndProcessing() {
        foreach (var unit in ResolveUnitNames()) {
            // `systemctl show` is the machine-readable form: key=value, one
            // per line, and it exits zero for a unit that does not exist
            // rather than making absence an error condition.
            var properties = Cli.ParseProperties(
                Cli.RunChecked(
                    "systemctl",
                    "show",
                    unit,
                    "--property=Id,Description,LoadState,ActiveState,SubState,UnitFileState,MainPID"
                )
            );

            var loadState = properties.GetValueOrDefault("LoadState", "not-found");
            if (loadState == "not-found") {
                WriteError(
                    new ErrorRecord(
                        new ItemNotFoundException($"no such service: {unit}"),
                        "ServiceNotFound",
                        ErrorCategory.ObjectNotFound,
                        unit
                    )
                );
                continue;
            }

            var service = new TrinixService {
                Name = properties.GetValueOrDefault("Id", unit),
                Description = properties.GetValueOrDefault("Description", string.Empty),
                State = properties.GetValueOrDefault("ActiveState", "unknown"),
                SubState = properties.GetValueOrDefault("SubState", "unknown"),
                Enabled = properties.GetValueOrDefault("UnitFileState", "unknown"),
                ProcessId = int.TryParse(properties.GetValueOrDefault("MainPID", "0"), out var pid) && pid > 0
                    ? pid
                    : null
            };

            if (!Failed || !service.IsHealthy) {
                WriteObject(service);
            }
        }
    }
}

/// <summary>
///     Restarts a system service.
/// </summary>
/// <example>
///     <code>Restart-TrinixService trinixd</code>
/// </example>
[Cmdlet(VerbsLifecycle.Restart, "TrinixService", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(TrinixService))]
public sealed class RestartTrinixServiceCommand : PSCmdlet {
    /// <summary>Service to restart. The <c>.service</c> suffix is optional.</summary>
    [Parameter(Position = 0, Mandatory = true, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    public required string Name { get; set; }

    /// <summary>Emit the service's state once it has come back.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <summary>Restarts the service, honouring -WhatIf and -Confirm.</summary>
    protected override void ProcessRecord() {
        var unit = Name.Contains('.', StringComparison.Ordinal) ? Name : Name + ".service";

        // High confirm impact: restarting a service on the machine you are
        // logged into can be the last command of the session.
        if (!ShouldProcess(unit, "Restart")) {
            return;
        }

        try {
            Cli.RunChecked("systemctl", "restart", unit);
        } catch (InvalidOperationException e) {
            WriteError(new ErrorRecord(e, "RestartFailed", ErrorCategory.InvalidOperation, unit));
            return;
        }

        if (PassThru) {
            foreach (var result in InvokeCommand.InvokeScript($"Get-TrinixService -Name '{unit}'")) {
                WriteObject(result, enumerateCollection: false);
            }
        }
    }
}

/// <summary>
///     A system service and its current state.
/// </summary>
public sealed class TrinixService {
    /// <summary>Unit name, including the <c>.service</c> suffix.</summary>
    public required string Name { get; init; }

    /// <summary>What the unit says it is for.</summary>
    public required string Description { get; init; }

    /// <summary>systemd's active state: active, inactive, failed, activating.</summary>
    public required string State { get; init; }

    /// <summary>The finer-grained state: running, exited, dead, start-pre.</summary>
    public required string SubState { get; init; }

    /// <summary>Whether the unit is enabled, disabled, static or masked.</summary>
    public required string Enabled { get; init; }

    /// <summary>Main process ID, or <see langword="null" /> when not running.</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    ///     Whether the service is doing what it is supposed to.
    /// </summary>
    /// <remarks>
    ///     A one-shot unit that ran and exited is healthy, and so is a service
    ///     that is running. Anything failed is not. This is the property worth
    ///     filtering on, which is why it exists rather than leaving every caller
    ///     to rediscover that <c>active/exited</c> is fine.
    /// </remarks>
    public bool IsHealthy =>
        State is "active" or "reloading"
        || (State == "inactive" && SubState == "dead" && Enabled is "static" or "disabled");
}

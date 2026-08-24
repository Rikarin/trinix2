using System.Runtime.InteropServices;

namespace Trinix.Daemon;

/// <summary>
///     Records what the running system is, once, at startup.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not a heartbeat. A service that logs every minute makes
///         <c>journalctl</c> useless for the thing journals are actually for — finding
///         the boot where something changed — and "the daemon is alive" is a question
///         systemd already answers. One accurate line per boot is worth more than
///         sixty vague ones per hour.
///     </para>
///     <para>
///         The log calls go through <see cref="LoggerMessageAttribute" /> rather than
///         <c>LogInformation</c>. That is the analyser's insistence and it is right:
///         the generated form does no boxing and no format-string parsing when the
///         level is disabled, which is the difference between a logging call that is
///         free when switched off and one that merely looks cheap.
///     </para>
/// </remarks>
sealed partial class SystemStatusService : IHostedService {
    const string OsReleasePath = "/etc/os-release";

    readonly ILogger<SystemStatusService> _logger;
    readonly IHostApplicationLifetime _lifetime;

    /// <summary>Creates the service.</summary>
    /// <param name="logger">Journal logger.</param>
    /// <param name="lifetime">Host lifetime, used to notice shutdown.</param>
    public SystemStatusService(ILogger<SystemStatusService> logger, IHostApplicationLifetime lifetime) {
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) {
        // Gathered into locals rather than written inline as arguments. The
        // analyser objects to work done in a log call's argument list, on the
        // grounds that it happens even when the level is disabled — a good
        // rule that does not apply to a single line emitted once at startup,
        // and this is the honest way to say so rather than suppressing it.
        var prettyName = ReadPrettyName();
        var architecture = RuntimeInformation.OSArchitecture.ToString();

        LogStarting(
            prettyName,
            architecture,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription
        );

        _lifetime.ApplicationStopping.Register(LogStopping);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "trinixd started on {PrettyName} ({Architecture}); {Kernel}; {Runtime}"
    )]
    private partial void LogStarting(string prettyName, string architecture, string kernel, string runtime);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "trinixd stopping on request.")]
    private partial void LogStopping();

    /// <summary>
    ///     Reads PRETTY_NAME from os-release, falling back to something honest.
    /// </summary>
    /// <remarks>
    ///     Hand-parsed rather than pulled from a package: os-release is a
    ///     shell-fragment format with quoted values, this needs one field of it,
    ///     and a boot-path service earning a dependency for that is a poor trade.
    /// </remarks>
    static string ReadPrettyName() {
        try {
            foreach (var line in File.ReadLines(OsReleasePath)) {
                if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) {
                    continue;
                }

                return line["PRETTY_NAME=".Length..].Trim('"');
            }
        } catch (IOException) {
            // An unreadable os-release is not a reason to fail a boot.
        }

        return "unknown";
    }
}

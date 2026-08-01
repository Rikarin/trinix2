using System.Diagnostics;
using System.Text;

namespace Trinix.Management;

/// <summary>
/// Runs a system command and captures what it said.
/// </summary>
/// <remarks>
/// <para>
/// systemd and iproute2 are addressed here by running their command-line tools
/// and reading structured output, rather than through their D-Bus and netlink
/// interfaces. That is a deliberate staging decision, not an oversight: both
/// protocols deserve real bindings in <c>Trinix.Interop</c> alongside the ones
/// Phase 4 needs for wlroots, and writing half of them now would mean writing
/// them twice.
/// </para>
/// <para>
/// What makes the interim tolerable is that both tools emit machine-readable
/// output — <c>systemctl show</c> in key=value form and <c>ip -json</c> — so
/// nothing here parses a human-facing table.
/// </para>
/// </remarks>
internal static class Cli
{
    /// <summary>
    /// Runs <paramref name="fileName"/> and returns its standard output.
    /// </summary>
    /// <param name="fileName">Executable to run; resolved through PATH.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <returns>Exit code and captured output.</returns>
    public static CommandResult Run(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {fileName}");

        // Read both streams before waiting. A process that fills the stderr
        // pipe while nothing drains it blocks forever, and `systemctl show` on
        // a busy system produces more than a pipe buffer's worth.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new CommandResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// Runs a command and throws if it failed.
    /// </summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments.</param>
    /// <returns>Standard output.</returns>
    /// <exception cref="InvalidOperationException">The command exited non-zero.</exception>
    public static string RunChecked(string fileName, params string[] arguments)
    {
        var result = Run(fileName, arguments);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(
                $"{fileName} exited {result.ExitCode}: {detail.Trim()}");
        }

        return result.StandardOutput;
    }

    /// <summary>
    /// Parses <c>key=value</c> output, as emitted by <c>systemctl show</c>.
    /// </summary>
    /// <param name="output">Raw command output.</param>
    /// <returns>The parsed properties.</returns>
    public static Dictionary<string, string> ParseProperties(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            // Last value wins, matching systemctl's own behaviour for
            // properties it repeats.
            properties[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
        }

        return properties;
    }

    /// <summary>
    /// Reads a whole file, returning <paramref name="fallback"/> if it cannot be read.
    /// </summary>
    /// <param name="path">File to read.</param>
    /// <param name="fallback">Value to return on failure.</param>
    /// <returns>The file's contents, trimmed, or the fallback.</returns>
    public static string ReadFileOrDefault(string path, string fallback)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    private static readonly char[] Newlines = ['\n', '\r'];

    /// <summary>
    /// Splits output into non-empty lines.
    /// </summary>
    /// <param name="output">Raw command output.</param>
    /// <returns>The lines, with blanks removed.</returns>
    public static string[] Lines(string output) =>
        output.Split(Newlines, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The outcome of running a command.
/// </summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Everything written to stdout.</param>
/// <param name="StandardError">Everything written to stderr.</param>
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

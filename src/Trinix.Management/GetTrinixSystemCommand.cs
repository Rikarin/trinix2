using System.Globalization;
using System.Management.Automation;
using System.Runtime.InteropServices;

namespace Trinix.Management;

/// <summary>
/// Describes the running system: what it is, what it booted from, how long ago.
/// </summary>
/// <remarks>
/// The equivalent of the window a mac user opens first. It answers "what am I
/// actually running" in one object, which on an A/B image system includes the
/// question no conventional distribution has to ask: which slot is this.
/// </remarks>
/// <example>
///   <code>Get-TrinixSystem</code>
/// </example>
[Cmdlet(VerbsCommon.Get, "TrinixSystem")]
[OutputType(typeof(TrinixSystemInfo))]
public sealed class GetTrinixSystemCommand : PSCmdlet
{
    /// <summary>Emits a single <see cref="TrinixSystemInfo"/>.</summary>
    protected override void EndProcessing()
    {
        var release = OsRelease.Read();

        WriteObject(new TrinixSystemInfo
        {
            Name = release.GetValueOrDefault("PRETTY_NAME", "Trinix"),
            Version = release.GetValueOrDefault("VERSION_ID", "unknown"),
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Kernel = Cli.ReadFileOrDefault("/proc/sys/kernel/osrelease", "unknown"),
            HostName = Cli.ReadFileOrDefault("/etc/hostname", Environment.MachineName),
            BootSlot = ReadBootSlot(),
            RootIsReadOnly = RootIsReadOnly(),
            Uptime = ReadUptime(),
            Runtime = RuntimeInformation.FrameworkDescription,
        });
    }

    /// <summary>
    /// Works out which image slot booted, from the kernel command line.
    /// </summary>
    /// <remarks>
    /// The partition label is the authority rather than anything written into
    /// the image, because the image is identical in both slots — that is the
    /// entire point of A/B — so a slot recorded at build time would be wrong
    /// half the time.
    /// </remarks>
    private static string ReadBootSlot()
    {
        var cmdline = Cli.ReadFileOrDefault("/proc/cmdline", string.Empty);

        foreach (var argument in cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!argument.StartsWith("root=", StringComparison.Ordinal))
            {
                continue;
            }

            // root=PARTUUID=<guid>, and Trinix's slot GUIDs end in ...000a or
            // ...000b (see image/scripts/build-image.sh).
            var value = argument["root=".Length..];
            if (value.EndsWith('a'))
            {
                return "A";
            }

            if (value.EndsWith('b'))
            {
                return "B";
            }

            return value;
        }

        return "unknown";
    }

    private static bool RootIsReadOnly()
    {
        foreach (var line in Cli.Lines(Cli.ReadFileOrDefault("/proc/self/mounts", string.Empty)))
        {
            var fields = line.Split(' ');
            if (fields.Length >= 4 && fields[1] == "/")
            {
                return fields[3].Split(',').Contains("ro");
            }
        }

        return false;
    }

    private static TimeSpan ReadUptime()
    {
        var uptime = Cli.ReadFileOrDefault("/proc/uptime", string.Empty).Split(' ');

        return uptime.Length > 0
            && double.TryParse(uptime[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
    }
}

/// <summary>
/// A snapshot of the running system.
/// </summary>
public sealed class TrinixSystemInfo
{
    /// <summary>Human-readable name, from <c>PRETTY_NAME</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Release version, from <c>VERSION_ID</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Processor architecture the system is running on.</summary>
    public required string Architecture { get; init; }

    /// <summary>Running kernel release.</summary>
    public required string Kernel { get; init; }

    /// <summary>Configured host name.</summary>
    public required string HostName { get; init; }

    /// <summary>Which A/B image slot booted: <c>A</c>, <c>B</c>, or unknown.</summary>
    public required string BootSlot { get; init; }

    /// <summary>Whether the root filesystem is mounted read-only, as it should be.</summary>
    public required bool RootIsReadOnly { get; init; }

    /// <summary>Time since boot.</summary>
    public required TimeSpan Uptime { get; init; }

    /// <summary>The .NET runtime hosting this session.</summary>
    public required string Runtime { get; init; }
}

/// <summary>
/// Reads <c>/etc/os-release</c>.
/// </summary>
internal static class OsRelease
{
    /// <summary>Parses os-release into a dictionary, empty if unreadable.</summary>
    public static Dictionary<string, string> Read()
    {
        var contents = Cli.ReadFileOrDefault("/etc/os-release", string.Empty);
        var values = Cli.ParseProperties(contents);

        // os-release quotes values that contain spaces; nothing downstream
        // wants to see the quotes.
        foreach (var key in values.Keys)
        {
            values[key] = values[key].Trim('"');
        }

        return values;
    }
}

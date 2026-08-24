using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trinix.Management;

/// <summary>
///     Lists network interfaces and their addresses.
/// </summary>
/// <example>
///     <code>Get-TrinixNetworkInterface</code>
/// </example>
/// <example>
///     <code>(Get-TrinixNetworkInterface -Name eth0).Addresses</code>
/// </example>
[Cmdlet(VerbsCommon.Get, "TrinixNetworkInterface")]
[OutputType(typeof(TrinixNetworkInterface))]
public sealed class GetTrinixNetworkInterfaceCommand : PSCmdlet {
    /// <summary>Interface names to report on. Omit for all of them.</summary>
    [Parameter(Position = 0, ValueFromPipeline = true)]
    public string[]? Name { get; set; }

    /// <summary>Include the loopback interface, which is otherwise filtered out.</summary>
    [Parameter]
    public SwitchParameter IncludeLoopback { get; set; }

    /// <summary>Queries iproute2 and writes one object per interface.</summary>
    protected override void EndProcessing() {
        // `ip -json` is why this cmdlet is thirty lines rather than a regex
        // that breaks the first time iproute2 adjusts a column.
        var json = Cli.RunChecked("ip", "-json", "addr", "show");

        var links = JsonSerializer.Deserialize(json, IpJsonContext.Default.IpLinkArray) ?? [];

        foreach (var link in links) {
            if (Name is { Length: > 0 } && !Name.Contains(link.IfName, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            if (!IncludeLoopback && string.Equals(link.LinkType, "loopback", StringComparison.Ordinal)) {
                continue;
            }

            WriteObject(
                new TrinixNetworkInterface {
                    Name = link.IfName,
                    State = link.OperState,
                    MacAddress = link.Address,
                    Mtu = link.Mtu,
                    IsUp = link.Flags?.Contains("UP") ?? false,
                    Addresses = [.. (link.AddrInfo ?? []).Select(a => $"{a.Local}/{a.PrefixLen}")]
                }
            );
        }
    }
}

/// <summary>
///     A network interface as the kernel sees it.
/// </summary>
public sealed class TrinixNetworkInterface {
    /// <summary>Interface name, such as <c>eth0</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Operational state reported by the driver: up, down, unknown.</summary>
    public required string State { get; init; }

    /// <summary>Hardware address, where the interface has one.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Maximum transmission unit, in bytes.</summary>
    public int Mtu { get; init; }

    /// <summary>Whether the interface is administratively up.</summary>
    public required bool IsUp { get; init; }

    /// <summary>Assigned addresses in CIDR form, v4 and v6 together.</summary>
    public required string[] Addresses { get; init; }
}

// The wire shape of `ip -json addr`. Source-generated rather than reflective,
// so this module keeps working if it is ever loaded by a trimmed host.
sealed class IpLink {
    [JsonPropertyName("ifname")]
    public string IfName { get; set; } = string.Empty;

    [JsonPropertyName("operstate")]
    public string OperState { get; set; } = "unknown";

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; }

    [JsonPropertyName("flags")]
    public string[]? Flags { get; set; }

    [JsonPropertyName("link_type")]
    public string? LinkType { get; set; }

    [JsonPropertyName("addr_info")]
    public IpAddrInfo[]? AddrInfo { get; set; }
}

sealed class IpAddrInfo {
    [JsonPropertyName("local")]
    public string Local { get; set; } = string.Empty;

    [JsonPropertyName("prefixlen")]
    public int PrefixLen { get; set; }
}

[JsonSerializable(typeof(IpLink[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
sealed partial class IpJsonContext : JsonSerializerContext { }

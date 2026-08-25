using System.Text.Json;

namespace Trinix.Management.Tests;

/// <summary>
///     The shape of <c>ip -json addr show</c>, as this module believes it to be.
/// </summary>
/// <remarks>
///     <para>
///         This is the belief that would otherwise be checked only by booting a VM.
///         <c>Get-TrinixNetworkInterface</c> is thirty lines rather than a regex
///         precisely because iproute2 emits JSON — but a JSON contract is still a
///         contract, and <c>link_type</c>, <c>addr_info</c> and <c>prefixlen</c> are
///         names that a property renamed in C# would silently stop matching, leaving a
///         cmdlet that reports every interface as down with no addresses.
///     </para>
///     <para>
///         The fixture is genuine <c>ip -json addr show</c> output from a Linux host,
///         trimmed to two interfaces. ⚠ Do not tidy it: the fields this code ignores
///         are as much part of the test as the ones it reads, because they are what
///         prove the deserialiser tolerates them.
///     </para>
/// </remarks>
public class IpJsonTests {
    const string Sample = """
        [
          {
            "ifindex": 1,
            "ifname": "lo",
            "flags": ["LOOPBACK","UP","LOWER_UP"],
            "mtu": 65536,
            "qdisc": "noqueue",
            "operstate": "UNKNOWN",
            "group": "default",
            "txqlen": 1000,
            "link_type": "loopback",
            "address": "00:00:00:00:00:00",
            "broadcast": "00:00:00:00:00:00",
            "addr_info": [
              { "family": "inet", "local": "127.0.0.1", "prefixlen": 8, "scope": "host", "label": "lo" },
              { "family": "inet6", "local": "::1", "prefixlen": 128, "scope": "host" }
            ]
          },
          {
            "ifindex": 2,
            "ifname": "eth0",
            "flags": ["BROADCAST","MULTICAST","UP","LOWER_UP"],
            "mtu": 1500,
            "qdisc": "fq_codel",
            "operstate": "UP",
            "group": "default",
            "txqlen": 1000,
            "link_type": "ether",
            "address": "52:54:00:12:34:56",
            "broadcast": "ff:ff:ff:ff:ff:ff",
            "addr_info": [
              { "family": "inet", "local": "10.0.2.15", "prefixlen": 24, "scope": "global", "label": "eth0" }
            ]
          }
        ]
        """;

    static IpLink[] Parse(string json) =>
        JsonSerializer.Deserialize(json, IpJsonContext.Default.IpLinkArray) ?? [];

    [Fact]
    public void ReadsEveryFieldTheCmdletUses() {
        var links = Parse(Sample);

        var eth0 = links.Single(l => l.IfName == "eth0");
        Assert.Equal("UP", eth0.OperState);
        Assert.Equal("52:54:00:12:34:56", eth0.Address);
        Assert.Equal(1500, eth0.Mtu);
        Assert.Equal("ether", eth0.LinkType);
        Assert.Contains("UP", eth0.Flags!);
        Assert.Equal("10.0.2.15", eth0.AddrInfo!.Single().Local);
        Assert.Equal(24, eth0.AddrInfo!.Single().PrefixLen);
    }

    [Fact]
    public void ReadsTheSnakeCaseNamesIprouteActuallyEmits() {
        // ⚠ link_type and addr_info are the two that a naive camelCase
        // convention would miss, and missing them is not an error — it is a
        // null, which reads downstream as "no addresses, not a loopback".
        var lo = Parse(Sample).Single(l => l.IfName == "lo");

        Assert.Equal("loopback", lo.LinkType);
        Assert.NotNull(lo.AddrInfo);
        Assert.Equal(2, lo.AddrInfo.Length);
    }

    [Fact]
    public void KeepsIpv4AndIpv6AddressesTogether() {
        // The cmdlet reports them in one list on purpose; a machine has one set
        // of addresses, not two.
        var lo = Parse(Sample).Single(l => l.IfName == "lo");

        Assert.Equal(
            ["127.0.0.1/8", "::1/128"],
            lo.AddrInfo!.Select(a => $"{a.Local}/{a.PrefixLen}").ToArray()
        );
    }

    [Fact]
    public void AnInterfaceWithNoAddressesParsesToNullRatherThanFailing() {
        // iproute2 omits addr_info entirely for an unconfigured interface.
        var links = Parse("""[{ "ifname": "eth1", "operstate": "DOWN", "mtu": 1500, "link_type": "ether" }]""");

        var link = Assert.Single(links);
        Assert.Null(link.AddrInfo);
        Assert.Null(link.Address);
        Assert.Null(link.Flags);
    }

    [Fact]
    public void UnknownFieldsAreIgnoredRatherThanRejected() {
        // iproute2 adds fields between releases; a strict reader would turn
        // every such release into an outage.
        var links = Parse(
            """[{ "ifname": "eth0", "operstate": "UP", "something_new_in_iproute2": { "nested": true } }]"""
        );

        Assert.Equal("eth0", Assert.Single(links).IfName);
    }

    [Fact]
    public void AMissingOperstateDefaultsToUnknownRatherThanNull() {
        // TrinixNetworkInterface.State is non-nullable, so the default has to
        // come from somewhere; "unknown" is also what iproute2 itself prints.
        var link = Assert.Single(Parse("""[{ "ifname": "eth0" }]"""));

        Assert.Equal("unknown", link.OperState);
        Assert.Equal(string.Empty, Parse("""[{}]""").Single().IfName);
    }

    [Fact]
    public void AnEmptyArrayIsAMachineWithNoInterfacesRatherThanAnError() {
        Assert.Empty(Parse("[]"));
    }

    [Fact]
    public void MalformedJsonThrowsRatherThanReturningSomethingPlausible() {
        // The cmdlet lets this propagate, which is right: `ip` producing
        // unparseable output is a broken system, not an interface report of
        // zero interfaces.
        Assert.Throws<JsonException>(() => Parse("not json at all"));
    }
}

namespace Trinix.Sdk.Generators.Tests;

/// <summary>
///     The contracts the generator refuses, and doc 01's rules that are only rules because
///     it does.
/// </summary>
/// <remarks>
///     ⚠ These are the tests that make the difference between a design document and a
///     constraint. Doc 01 § The service surface says every member is <c>async</c> and that
///     there is no <c>IsPermissionGranted</c> boolean; nothing in the type system says
///     either, so the only place they can be enforced is the one thing every service
///     contract passes through. A test per rule is what stops the enforcement from being
///     quietly relaxed by whoever finds it inconvenient.
/// </remarks>
public sealed class ContractRuleTests {
    const string Prologue = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Trinix.Services.Contracts;

        namespace Sample;

        """;

    static IReadOnlyList<string> Diagnose(string body) =>
        GeneratorHarness.Run(Prologue + body, expectFailure: true).Ids;

    const string Attribute = """
        [TrinixService("io.trinix.Sample", BusName = "io.trinix.Test", ObjectPath = "/io/trinix/Sample")]
        """;

    /// <summary>Doc 01: everything is async, even where the implementation is local.</summary>
    [Fact]
    public void ASynchronousMemberIsRefused() =>
        Assert.Contains("TRX1001", Diagnose(Attribute + """

            public interface ISample {
                string Describe(CancellationToken cancellationToken);
            }
            """));

    /// <summary>A call that crosses a process boundary must be stoppable.</summary>
    [Fact]
    public void AMemberWithNoCancellationTokenIsRefused() =>
        Assert.Contains("TRX1002", Diagnose(Attribute + """

            public interface ISample {
                Task<string> DescribeAsync();
            }
            """));

    /// <summary>A property getter is synchronous by construction, so it cannot be a bus call.</summary>
    [Fact]
    public void APropertyIsRefused() =>
        Assert.Contains("TRX1003", Diagnose(Attribute + """

            public interface ISample {
                string Name { get; }
            }
            """));

    /// <summary>
    ///     Doc 01: no <c>IsPermissionGranted</c> boolean. Do the thing and handle the refusal.
    /// </summary>
    /// <remarks>
    ///     ⚠ The check is a name containing "Permission" plus a <c>Task&lt;bool&gt;</c>
    ///     return, which is a heuristic and is stated as one. It cannot catch a query named
    ///     <c>CanIAsync</c>, and it is not trying to: the value is that the obvious spelling
    ///     — the one somebody reaches for at speed, having half-remembered another platform
    ///     — stops at the compiler with the reason attached.
    /// </remarks>
    [Fact]
    public void APermissionQueryIsRefused() =>
        Assert.Contains("TRX1004", Diagnose(Attribute + """

            public interface ISample {
                Task<bool> IsPermissionGrantedAsync(string permission, CancellationToken cancellationToken);
            }
            """));

    /// <summary>A type with no D-Bus representation is a compile error, not a run-time surprise.</summary>
    [Fact]
    public void AnUnmarshallableParameterIsRefused() =>
        Assert.Contains("TRX1005", Diagnose(Attribute + """

            public interface ISample {
                Task SendAsync(System.Net.IPAddress address, CancellationToken cancellationToken);
            }
            """));

    /// <summary>A record that contains itself has no fixed shape and so no signature.</summary>
    [Fact]
    public void ASelfReferentialRecordIsRefused() =>
        Assert.Contains("TRX1005", Diagnose("""
            [ServiceRecord]
            public sealed record Node(string Name, IReadOnlyList<Node> Children);
            """));

    /// <summary>A signal subscription has exactly one spelling.</summary>
    [Fact]
    public void AMalformedSignalIsRefused() =>
        Assert.Contains("TRX1006", Diagnose(Attribute + """

            public interface ISample {
                [ServiceSignal("Changed")]
                Task<IDisposable> WatchChangedAsync(Action<string> handler, Action<string> second, CancellationToken cancellationToken);
            }
            """));

    /// <summary>
    ///     A service that names its interface and not the process that serves it is
    ///     incomplete.
    /// </summary>
    /// <remarks>
    ///     Doc 02: notifications and the clipboard are both <c>io.trinix.Shell</c>, so the
    ///     bus name is not derivable from the interface name and defaulting it would produce
    ///     a proxy that calls a bus name nobody owns.
    /// </remarks>
    [Fact]
    public void AServiceWithNoBusNameIsRefused() =>
        Assert.Contains("TRX1007", Diagnose("""
            [TrinixService("io.trinix.Sample", ObjectPath = "/io/trinix/Sample")]
            public interface ISample {
                Task ResetAsync(CancellationToken cancellationToken);
            }
            """));
}

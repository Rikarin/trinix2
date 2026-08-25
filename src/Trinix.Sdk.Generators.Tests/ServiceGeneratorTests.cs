namespace Trinix.Sdk.Generators.Tests;

/// <summary>
///     What the generator emits for a contract that is well-formed.
/// </summary>
/// <remarks>
///     The sample below is not a Trinix service; it is the smallest contract that exercises
///     every marshalling case the generator has — a nested record, a sequence of records, an
///     optional record, a duration, a method with no arguments, a method with a reply, and a
///     signal. A test suite written against <c>INotifications</c> would be a suite that
///     breaks when a notification's fields change, which is a thing that should be allowed
///     to happen without a generator test failing.
/// </remarks>
public sealed class ServiceGeneratorTests {
    const string Sample = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Trinix.Services.Contracts;

        namespace Sample;

        [ServiceRecord]
        public sealed record Point(int X, int Y);

        [ServiceRecord]
        public sealed record Shape(string Name, IReadOnlyList<Point> Points, Point? Origin, TimeSpan Age);

        [TrinixService(
            "io.trinix.Sample",
            BusName = "io.trinix.Test",
            ObjectPath = "/io/trinix/Sample",
            Version = 3)]
        public interface ISample {
            Task<Shape> DescribeAsync(string name, CancellationToken cancellationToken);

            Task ResetAsync(CancellationToken cancellationToken);

            [ServiceSignal("Changed")]
            Task<IDisposable> WatchChangedAsync(Action<Shape> handler, CancellationToken cancellationToken);
        }
        """;

    static readonly GeneratorResult Generated = GeneratorHarness.Run(Sample);

    /// <summary>
    ///     The whole file is synchronous, which is the <c>ref struct</c> constraint stated as
    ///     an assertion.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the load-bearing test of the entire generator. <c>MessageWriter</c> is a
    ///     <c>ref struct</c> and cannot cross an <c>await</c>; the proxy satisfies that by
    ///     containing no <c>async</c> method at all — it builds the message, sends it, and
    ///     hands the transport's task to <c>ServiceErrors.TranslateAsync</c>. Grepping for
    ///     the keyword is crude and is exactly right: any future edit that makes a proxy
    ///     method <c>async</c> reintroduces the failure, and no subtler assertion would
    ///     notice.
    /// </remarks>
    [Fact]
    public void ProxyContainsNoAsyncMethodAtAll() {
        var proxy = Generated.File("SampleProxy.g.cs");
        Assert.DoesNotContain("async", proxy, StringComparison.Ordinal);
        Assert.Contains("static global::Tmds.DBus.Protocol.MessageBuffer BuildDescribe(", proxy, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The dispatcher has exactly one <c>async</c> method, and the argument decoding is
    ///     not in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The symmetric half of the constraint, and the one that is easy to forget:
    ///     <c>Reader</c> is a <c>ref struct</c> too. The dispatcher must decode into ordinary
    ///     values <i>before</i> awaiting the implementation, which is why
    ///     <c>ReadDescribeArguments</c> exists as its own static method rather than being
    ///     three lines inside <c>HandleMethodAsync</c>.
    /// </remarks>
    [Fact]
    public void HandlerIsAsyncOnlyWhereItDispatches() {
        var handler = Generated.File("SampleHandler.g.cs");
        Assert.Equal(1, Occurrences(handler, "async "));
        Assert.Contains("public async global::System.Threading.Tasks.ValueTask HandleMethodAsync", handler, StringComparison.Ordinal);
        Assert.Contains("static string ReadDescribeArguments(", handler, StringComparison.Ordinal);
    }

    /// <summary>A record's signature is its primary constructor, in order, recursively.</summary>
    [Fact]
    public void NestedRecordSignatureIsItsMembersInOrder() {
        var wire = Generated.File("ServiceWire.g.cs");
        Assert.Contains("public const string PointSignature = \"(ii)\";", wire, StringComparison.Ordinal);
        Assert.Contains("public const string ShapeSignature = \"(sa(ii)a(ii)x)\";", wire, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An optional record is an array of zero or one, which is D-Bus's only "maybe".
    /// </summary>
    /// <remarks>
    ///     ⚠ The signature is identical to a real array's — both halves of
    ///     <c>a(ii)a(ii)</c> in <c>ShapeSignature</c> above — so the only thing that
    ///     distinguishes <c>IReadOnlyList&lt;Point&gt;</c> from <c>Point?</c> is the emitted
    ///     code. That is what makes this worth asserting separately from the signature.
    /// </remarks>
    [Fact]
    public void OptionalRecordIsWrittenAsAnArrayOfZeroOrOne() {
        var wire = Generated.File("ServiceWire.g.cs");
        Assert.Contains("if (value.Origin is not null)", wire, StringComparison.Ordinal);
        Assert.Contains("WritePoint(ref writer, value.Origin);", wire, StringComparison.Ordinal);
    }

    /// <summary>Every structure read realigns first.</summary>
    /// <remarks>
    ///     ⚠ D-Bus structures are 8-aligned and the alignment is not implied by the members.
    ///     A reader that skips it is correct for exactly the structures whose first member is
    ///     already aligned — which is the subset large enough to make the bug survive a
    ///     casual test and small enough to break in production.
    /// </remarks>
    [Fact]
    public void EveryStructureReaderAligns() {
        var wire = Generated.File("ServiceWire.g.cs");
        Assert.Equal(Occurrences(wire, "public static global::Sample."), Occurrences(wire, "reader.AlignStruct();"));
    }

    /// <summary>
    ///     A method with no arguments sends no signature field, and the dispatcher still
    ///     matches it.
    /// </summary>
    /// <remarks>
    ///     ⚠ These two are different literals for the same thing and that asymmetry is a
    ///     real trap: an absent SIGNATURE header field is not an empty one, so the writer is
    ///     handed <c>null</c>, while the dispatcher compares against the empty string it
    ///     read out of a header that has no such field. Spelling both <c>null</c> would make
    ///     every argument-less member unreachable; spelling both <c>""</c> would produce a
    ///     message the daemon drops.
    /// </remarks>
    [Fact]
    public void ArgumentlessMethodSendsNoSignatureAndStillDispatches() {
        Assert.Contains("\"Reset\", null!);", Generated.File("SampleProxy.g.cs"), StringComparison.Ordinal);
        Assert.Contains("string.Equals(signature, \"\",", Generated.File("SampleHandler.g.cs"), StringComparison.Ordinal);
    }

    /// <summary>The XML is derived from the C#, per doc 01, and carries the contract version.</summary>
    [Fact]
    public void IntrospectionXmlIsGeneratedFromTheInterface() {
        var introspection = Generated.File("SampleIntrospection.g.cs");
        Assert.Contains("<interface name=\\\"io.trinix.Sample\\\">", introspection, StringComparison.Ordinal);
        Assert.Contains("<annotation name=\\\"io.trinix.Version\\\" value=\\\"3\\\"/>", introspection, StringComparison.Ordinal);
        Assert.Contains("<arg name=\\\"name\\\" type=\\\"s\\\" direction=\\\"in\\\"/>", introspection, StringComparison.Ordinal);
        Assert.Contains("<arg name=\\\"result\\\" type=\\\"(sa(ii)a(ii)x)\\\" direction=\\\"out\\\"/>", introspection, StringComparison.Ordinal);
        Assert.Contains("<signal name=\\\"Changed\\\">", introspection, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A signal's subscription is pinned to the sender the proxy was pointed at.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without <c>Sender</c> on the match rule any peer on the bus could synthesise
    ///     the signal. For a notification activation that means any application could tell
    ///     another that the user pressed one of its buttons.
    /// </remarks>
    [Fact]
    public void SignalMatchRuleIsPinnedToTheSender() {
        var proxy = Generated.File("SampleProxy.g.cs");
        Assert.Contains("Sender = _destination,", proxy, StringComparison.Ordinal);
        Assert.Contains("Member = \"Changed\"", proxy, StringComparison.Ordinal);
    }

    /// <summary>A service raises its own signals; the handler gets the emitters, not a watch.</summary>
    [Fact]
    public void HandlerEmitsSignalsAndOffersADirectedOverload() {
        var handler = Generated.File("SampleHandler.g.cs");
        Assert.Contains("protected void EmitChanged(global::Sample.Shape value)", handler, StringComparison.Ordinal);
        Assert.Contains("protected void EmitChanged(string destination, global::Sample.Shape value)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchChangedAsync", handler, StringComparison.Ordinal);
    }

    static int Occurrences(string text, string needle) {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}

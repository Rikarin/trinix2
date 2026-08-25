using Tmds.DBus.Protocol;
using Trinix.Bundle;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     The generated proxy and the generated dispatcher, over real D-Bus bytes.
/// </summary>
/// <remarks>
///     Every case here sends a message through <see cref="LoopbackBus" />, which frames it,
///     relays it and stamps its sender exactly as a daemon would. What is being tested is
///     not that <c>Write…</c> and <c>Read…</c> are inverses — that would pass with both
///     halves wrong in the same way — but that a value survives being serialised, framed,
///     padded, aligned, transported, and decoded by the other half of one generated pair.
/// </remarks>
public sealed class RoundTripTests {
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Doc 02's notification record, every field, through the bus and back.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately not a smoke test: the request nests a record inside an array
    ///     inside a record, carries an optional record that is present, and mixes an enum
    ///     and a duration in among the strings. Those are the four cases where a
    ///     marshaller's alignment is wrong in a way that a single-string test cannot see.
    /// </remarks>
    [Fact]
    public async Task ANotificationSurvivesTheBus() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeNotifications(connection));
        var proxy = new NotificationsProxy(host.Client, LoopbackBus.ServiceName, NotificationsProxy.DefaultObjectPath);

        var request = new NotificationRequest(
            Title: "Download finished",
            Body: "trinix-0.3-arm64.tdi",
            Urgency: NotificationUrgency.Low,
            Actions: [new NotificationAction("reveal", "Show in Files"), new NotificationAction("open", "Open")],
            Reply: new NotificationReply("Reply…", "Send"),
            Group: "downloads",
            Expiry: TimeSpan.FromSeconds(30)
        );

        var id = await proxy.PostAsync(request, host.Deadline);

        Assert.Equal(new NotificationId("io.example.notes", 7), id);
        Assert.Equivalent(request, host.Service.LastRequest, strict: true);
    }

    /// <summary>
    ///     An absent optional record arrives absent, and an empty sequence arrives empty.
    /// </summary>
    /// <remarks>
    ///     ⚠ The empty cases are where an array's alignment padding is written and nothing
    ///     follows it, so a marshaller that pads by writing the first element gets these
    ///     wrong and only these. Doc 02's <c>reply?</c> is exactly this shape.
    /// </remarks>
    [Fact]
    public async Task AnAbsentReplyAndAnEmptyActionListSurviveTheBus() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeNotifications(connection));
        var proxy = new NotificationsProxy(host.Client, LoopbackBus.ServiceName, NotificationsProxy.DefaultObjectPath);

        var request = new NotificationRequest(
            Title: "Connected",
            Body: "",
            Urgency: NotificationUrgency.Normal,
            Actions: [],
            Reply: null,
            Group: "",
            // TimeSpan.Zero is "never expires", not "expire immediately" — see the contract.
            Expiry: TimeSpan.Zero
        );

        await proxy.PostAsync(request, host.Deadline);

        var received = host.Service.LastRequest;
        Assert.NotNull(received);
        Assert.Null(received.Reply);
        Assert.Empty(received.Actions);
        Assert.Equal(TimeSpan.Zero, received.Expiry);
        Assert.Equal("", received.Body);
    }

    /// <summary>Two arguments in one call arrive in the order they were written.</summary>
    /// <remarks>
    ///     ⚠ The one bug a same-signature swap produces silently. <c>Update</c> takes
    ///     <c>(su)(ssua(ss)a(ss)sx)</c>; a dispatcher that decoded the second argument first
    ///     would fail loudly, but a record whose members were reordered would not, which is
    ///     why the assertion is on the values and not just on the shape.
    /// </remarks>
    [Fact]
    public async Task ArgumentsArriveInOrder() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeNotifications(connection));
        var proxy = new NotificationsProxy(host.Client, LoopbackBus.ServiceName, NotificationsProxy.DefaultObjectPath);

        var id = new NotificationId("io.example.notes", 4294967295);
        var request = new NotificationRequest("Second", "Body", NotificationUrgency.Normal, [], null, "g", TimeSpan.Zero);

        await proxy.UpdateAsync(id, request, host.Deadline);

        Assert.Equal(id, host.Service.LastId);
        Assert.Equivalent(request, host.Service.LastRequest, strict: true);
    }

    /// <summary>
    ///     A refusal crosses the bus as a typed exception carrying the permission it wanted.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is doc 01's "no <c>IsPermissionGranted</c> boolean" rule, end to end. The
    ///     application posts what it means; the service throws; the error name and its
    ///     message become a D-Bus error reply; the proxy turns it back into a
    ///     <see cref="PermissionDeniedException" /> whose
    ///     <see cref="PermissionDeniedException.Permission" /> is a flag from the same
    ///     vocabulary <c>Info.json</c> is signed against. There is no code path in which the
    ///     caller asked first.
    /// </remarks>
    [Fact]
    public async Task ARefusalArrivesAsAPermissionDeniedExceptionNamingThePermission() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeNotifications(connection));
        var proxy = new NotificationsProxy(host.Client, LoopbackBus.ServiceName, NotificationsProxy.DefaultObjectPath);

        var critical = new NotificationRequest(
            "Battery critically low", "", NotificationUrgency.Critical, [], null, "", TimeSpan.Zero
        );

        var refusal = await Assert.ThrowsAsync<PermissionDeniedException>(
            () => proxy.PostAsync(critical, host.Deadline)
        );

        Assert.Equal(Permissions.SystemNotificationsCritical, refusal.Permission);
        Assert.Equal(BundlePermissions.SystemNotificationsCritical, refusal.PermissionName);
        Assert.Equal(ServiceErrors.PermissionDenied, refusal.ErrorName);
        Assert.Contains("pierces Focus", refusal.Explanation, StringComparison.Ordinal);
    }

    /// <summary>An error name Trinix defines but does not map keeps its name.</summary>
    /// <remarks>
    ///     Doc 02 makes update and withdraw first-class so that an application can drive one
    ///     notification through a long operation, which means it will routinely race the
    ///     user dismissing it. That race has to be distinguishable from a bug, and the
    ///     distinction is the error name surviving the trip.
    /// </remarks>
    [Fact]
    public async Task AMissingObjectKeepsItsErrorName() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeNotifications(connection));
        host.Service.NotificationIsGone = true;
        var proxy = new NotificationsProxy(host.Client, LoopbackBus.ServiceName, NotificationsProxy.DefaultObjectPath);

        var failure = await Assert.ThrowsAsync<TrinixServiceException>(() => proxy.UpdateAsync(
            new NotificationId("io.example.notes", 1),
            new NotificationRequest("t", "b", NotificationUrgency.Normal, [], null, "", TimeSpan.Zero),
            host.Deadline
        ));

        Assert.Equal(ServiceErrors.NoSuchObject, failure.ErrorName);
    }

    /// <summary>A signal sent to one peer arrives at that peer's subscription.</summary>
    /// <remarks>
    ///     ⚠ Directed, not broadcast, because a notification activation is scoped to one
    ///     application: broadcasting it would tell every application on the bus which button
    ///     the user pressed in another. The proxy's match rule pins the sender, and
    ///     <see cref="LoopbackBus" /> stamps it the way a daemon does — which is what makes
    ///     that pin something other than decoration.
    /// </remarks>
    [Fact]
    public async Task ADirectedSignalReachesItsSubscriber() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeNotifications(connection));
        var proxy = new NotificationsProxy(host.Client, LoopbackBus.ServiceName, NotificationsProxy.DefaultObjectPath);

        var arrived = new TaskCompletionSource<NotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await proxy.WatchActivatedAsync(activation => arrived.TrySetResult(activation), host.Deadline);

        var expected = new NotificationActivation(new NotificationId("io.example.notes", 7), "reveal");
        host.Service.Activate(LoopbackBus.ClientName, expected);

        Assert.Equal(expected, await arrived.Task.WaitAsync(Patience));
    }

    /// <summary>A broadcast signal reaches a subscriber too.</summary>
    [Fact]
    public async Task ABroadcastSignalReachesASubscriber() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeSystem(connection));
        var proxy = new SystemProxy(host.Client, LoopbackBus.ServiceName, SystemProxy.DefaultObjectPath);

        var arrived = new TaskCompletionSource<Appearance>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await proxy.WatchAppearanceChangedAsync(a => arrived.TrySetResult(a), host.Deadline);

        host.Service.Appearance = new Appearance(ColorScheme.Light, "#c0392b", ReduceMotion: false, IncreaseContrast: true);
        host.Service.Change();

        Assert.Equal(host.Service.Appearance, await arrived.Task.WaitAsync(Patience));
    }

    /// <summary>A method with no arguments still reaches its member.</summary>
    /// <remarks>
    ///     ⚠ The case an absent-versus-empty signature bug breaks and nothing else does. A
    ///     no-argument call carries no SIGNATURE header field at all, and the dispatcher
    ///     matches against the empty string it reads back out.
    /// </remarks>
    [Fact]
    public async Task AnArgumentlessCallReachesItsMember() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeSystem(connection));
        var proxy = new SystemProxy(host.Client, LoopbackBus.ServiceName, SystemProxy.DefaultObjectPath);

        var release = await proxy.GetReleaseAsync(host.Deadline);

        Assert.Equal(new SystemRelease("0.3", "20260825.1", "Trinix 0.3"), release);
    }

    /// <summary>A record whose first member is an array survives, empty and not.</summary>
    /// <remarks>
    ///     <c>ClipboardOffer</c> is <c>(asbt)</c>: an array, then a bool, then a 64-bit
    ///     integer that has to be 8-aligned after them. ⚠ The alignment after a
    ///     variable-length member is the thing this asserts, and it is only wrong for some
    ///     lengths — hence both a populated and an empty case.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AnOfferSurvivesWhateverItsLength(int types) {
        await using var host = await ServiceHost.StartAsync(connection => new FakeClipboard(connection));
        host.Service.Offer = new ClipboardOffer(
            [.. Enumerable.Range(0, types).Select(index => $"application/x-trinix-{index}")],
            Sensitive: true,
            Serial: ulong.MaxValue
        );
        var proxy = new ClipboardProxy(host.Client, LoopbackBus.ServiceName, ClipboardProxy.DefaultObjectPath);

        var offer = await proxy.GetOfferAsync(host.Deadline);

        Assert.Equivalent(host.Service.Offer, offer, strict: true);
    }

    /// <summary>
    ///     The introspection XML a running service answers with is the one generated from
    ///     the C#.
    /// </summary>
    /// <remarks>
    ///     Doc 01: the C# interface is the source of truth and the XML is derived from it.
    ///     This asserts the derivation reaches the wire — that <c>busctl introspect</c>
    ///     against a Trinix service would print what the contract says — rather than only
    ///     that a constant has the right characters in it.
    /// </remarks>
    [Fact]
    public async Task IntrospectionOverTheBusMatchesTheGeneratedXml() {
        await using var host = await ServiceHost.StartAsync(connection => new FakeSystem(connection));

        var message = BuildIntrospect(host.Client, LoopbackBus.ServiceName, SystemProxy.DefaultObjectPath);
        var xml = await host.Client.CallMethodAsync(
            message,
            static (Message reply, object? state) => {
                var reader = reply.GetBodyReader();
                return reader.ReadString();
            },
            null!
        ).WaitAsync(Patience);

        Assert.Contains(SystemIntrospection.InterfaceXml, xml, StringComparison.Ordinal);
        Assert.Contains("org.freedesktop.DBus.Introspectable", xml, StringComparison.Ordinal);
        Assert.Contains("<annotation name=\"io.trinix.Version\" value=\"1\"/>", xml, StringComparison.Ordinal);
    }

    // ⚠ Non-async and static, for the same reason every generated builder is: MessageWriter
    // is a ref struct and cannot be in scope across an await.
    static MessageBuffer BuildIntrospect(DBusConnection connection, string destination, string path) {
        var writer = connection.GetMessageWriter();
        try {
            writer.WriteMethodCallHeader(
                destination, path, "org.freedesktop.DBus.Introspectable", "Introspect", null!
            );
            return writer.CreateMessage();
        } finally {
            writer.Dispose();
        }
    }
}

using Tmds.DBus.Protocol;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     A bus, a service on it, and a client connection, for the duration of one test.
/// </summary>
/// <typeparam name="T">The generated handler's subclass, so a test can drive the fake directly.</typeparam>
/// <remarks>
///     ⚠ <see cref="Deadline" /> is a real timeout, not <see cref="CancellationToken.None" />.
///     Every method on a service contract takes a token because the far side may be
///     restarting; a test that passed <c>None</c> and then met a harness bug would hang the
///     suite rather than fail it, and a suite that hangs in CI is a suite somebody
///     eventually disables.
/// </remarks>
sealed class ServiceHost<T> : IAsyncDisposable where T : IPathMethodHandler {
    ServiceHost(LoopbackBus bus, DBusConnection service, DBusConnection client, T handler) {
        Bus = bus;
        Service = handler;
        Client = client;
        _service = service;
    }

    readonly DBusConnection _service;
    readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));

    /// <summary>The relay, so a test can assert it did not fall over.</summary>
    public LoopbackBus Bus { get; }

    /// <summary>The fake service, to set up and to assert on.</summary>
    public T Service { get; }

    /// <summary>The connection a proxy should be built on.</summary>
    public DBusConnection Client { get; }

    /// <summary>A token that fails the test rather than hanging it.</summary>
    public CancellationToken Deadline => _deadline.Token;

    /// <summary>Stand up a bus with one service published on it.</summary>
    /// <param name="create">Builds the handler once its connection exists.</param>
    public static async Task<ServiceHost<T>> StartAsync(Func<DBusConnection, T> create) {
        var bus = new LoopbackBus();
        var service = await bus.ConnectServiceAsync();
        var client = await bus.ConnectClientAsync();

        // ⚠ After ConnectAsync, not before: AddMethodHandler throws "Connect before using
        // this method" on a connection that has not completed its handshake.
        var handler = create(service);
        service.AddMethodHandler(handler);

        return new ServiceHost<T>(bus, service, client, handler);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        var failures = Bus.Failures;
        _deadline.Dispose();
        Client.Dispose();
        _service.Dispose();
        Bus.Dispose();

        // A relay that threw means the test's result was decided by the harness rather than
        // by the code under test, which is worth failing on even when the assertions passed.
        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(failure => failure.ToString())));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Type inference for <see cref="ServiceHost{T}" />.</summary>
static class ServiceHost {
    /// <summary>Stand up a bus with one service published on it.</summary>
    /// <typeparam name="T">The generated handler's subclass.</typeparam>
    /// <param name="create">Builds the handler once its connection exists.</param>
    public static Task<ServiceHost<T>> StartAsync<T>(Func<DBusConnection, T> create)
        where T : IPathMethodHandler => ServiceHost<T>.StartAsync(create);
}

using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using Tmds.DBus.Protocol;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     A two-peer D-Bus daemon, in memory, for tests.
/// </summary>
/// <remarks>
///     <para>
///         This exists because of one asymmetry in <c>Tmds.DBus.Protocol</c>: every
///         <see cref="DBusConnection" /> performs the <i>client</i> half of the SASL
///         handshake, so two of them wired back to back both send <c>AUTH</c> and neither
///         answers. Handing each one a stream that goes to something speaking the server
///         half is the only way to get a real message across without a real
///         <c>dbus-daemon</c> — and there is no <c>dbus-daemon</c> on macOS, which is where
///         this suite runs.
///     </para>
///     <para>
///         What that buys is the thing a marshalling test cannot fake: the generated proxy
///         and the generated dispatcher exchanging <b>actual bytes</b>, framed, aligned and
///         padded by the same library that will do it on the device. A test that called
///         <c>WriteNotificationRequest</c> and then <c>ReadNotificationRequest</c> in
///         process would agree with itself about a wrong alignment.
///     </para>
///     <para>
///         ⚠ <b>What it is not.</b> There is no policy, no activation, no name arbitration
///         beyond two hard-coded unique names, and — the one that matters — <b>no file
///         descriptor passing</b>. Descriptors travel in <c>SCM_RIGHTS</c> on a Unix socket
///         and there is no socket here, so <see cref="IClipboard.ReadAsync" /> and
///         <see cref="IClipboard.OfferAsync" /> cannot be exercised end to end by this
///         harness. Doc 18 R4 proved <c>h</c> in both directions, including a live socket,
///         against a real daemon in a Linux container; that evidence is not re-derived here
///         and this file should not be read as if it were.
///     </para>
///     <para>
///         It is also the seed of doc 01's <c>Trinix.Sdk.Testing</c> — "a fake service host,
///         so an application's tests need no session". It lives in a test project until a
///         second consumer wants it, because a shipped assembly is a promise and this is
///         currently one suite's tool.
///     </para>
/// </remarks>
sealed class LoopbackBus : IDisposable {
    /// <summary>The unique name the bus gives the peer that publishes the service.</summary>
    public const string ServiceName = ":1.0";

    /// <summary>The unique name the bus gives the peer that calls it.</summary>
    public const string ClientName = ":1.1";

    readonly Stream[] _busSide = new Stream[2];
    readonly Stream[] _peerSide = new Stream[2];
    readonly string[] _names = [ServiceName, ClientName];
    readonly List<Exception> _failures = [];

    /// <summary>Wire up two peers and start relaying.</summary>
    public LoopbackBus() {
        for (var index = 0; index < 2; index++) {
            var toPeer = new Pipe();
            var toBus = new Pipe();
            _busSide[index] = new DuplexStream(toBus.Reader.AsStream(), toPeer.Writer.AsStream());
            _peerSide[index] = new DuplexStream(toPeer.Reader.AsStream(), toBus.Writer.AsStream());
        }

        _ = PumpAsync(0);
        _ = PumpAsync(1);
    }

    /// <summary>Anything the relay threw. A test that ends with entries here has a bug in the harness.</summary>
    public IReadOnlyList<Exception> Failures {
        get {
            lock (_failures) {
                return [.. _failures];
            }
        }
    }

    /// <summary>Connect the peer that will publish a handler.</summary>
    public Task<DBusConnection> ConnectServiceAsync() => ConnectAsync(0);

    /// <summary>Connect the peer that will call it.</summary>
    public Task<DBusConnection> ConnectClientAsync() => ConnectAsync(1);

    async Task<DBusConnection> ConnectAsync(int index) {
        var connection = new DBusConnection(new StreamOptions(_peerSide[index]));
        await connection.ConnectAsync();
        return connection;
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var stream in _busSide.Concat(_peerSide)) {
            stream.Dispose();
        }
    }

    async Task PumpAsync(int index) {
        var me = _busSide[index];
        var other = _busSide[1 - index];
        try {
            await AuthenticateAsync(me);
            while (true) {
                var message = await ReadMessageAsync(me);
                if (message is null) {
                    return;
                }

                var header = Header.Parse(message);
                if (header.Destination == "org.freedesktop.DBus") {
                    await me.WriteAsync(BusReply(header, _names[index], _names[1 - index]));
                    continue;
                }

                // ⚠ The bus, not the sender, sets SENDER — that is what makes the field
                // trustworthy, and it is why a generated proxy can pin its match rule to a
                // sender at all. Relaying the bytes untouched would leave every signal
                // unattributed and every match rule with a Sender would silently match
                // nothing, which would look exactly like a marshalling bug.
                await other.WriteAsync(Header.WithSender(message, header, _names[index]));
            }
        } catch (ObjectDisposedException) {
            // The test finished and disposed the streams underneath us.
        } catch (InvalidOperationException) {
            // Same, from the Pipe side.
        } catch (Exception failure) {
            lock (_failures) {
                _failures.Add(failure);
            }
        }
    }

    // ------------------------------------------------------------------ SASL

    static async Task AuthenticateAsync(Stream stream) {
        await ReadExactlyAsync(stream, new byte[1]); // the NUL credentials byte
        while (true) {
            var line = await ReadLineAsync(stream);
            if (line is null) {
                return;
            }

            if (line.StartsWith("AUTH", StringComparison.Ordinal)) {
                await SendAsync(stream, "OK 0123456789abcdef0123456789abcdef\r\n");
            } else if (line.StartsWith("NEGOTIATE_UNIX_FD", StringComparison.Ordinal)) {
                // ⚠ Refused, honestly: there is no socket under this stream, so agreeing
                // would let a caller send an `h` that could never arrive.
                await SendAsync(stream, "ERROR no fd passing over a pipe\r\n");
            } else if (line.StartsWith("BEGIN", StringComparison.Ordinal)) {
                return;
            } else {
                await SendAsync(stream, "ERROR\r\n");
            }
        }
    }

    static Task SendAsync(Stream stream, string text) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    static async Task<string?> ReadLineAsync(Stream stream) {
        var text = new StringBuilder();
        var one = new byte[1];
        while (true) {
            if (await stream.ReadAsync(one) == 0) {
                return null;
            }

            if (one[0] == (byte)'\n') {
                return text.ToString().TrimEnd('\r');
            }

            text.Append((char)one[0]);
        }
    }

    // --------------------------------------------------------------- framing

    static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer) {
        var read = 0;
        while (read < buffer.Length) {
            var count = await stream.ReadAsync(buffer[read..]);
            if (count == 0) {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }

    static async Task<byte[]?> ReadMessageAsync(Stream stream) {
        var head = new byte[16];
        try {
            await ReadExactlyAsync(stream, head);
        } catch (EndOfStreamException) {
            return null;
        }

        var little = head[0] == (byte)'l';
        var bodyLength = Header.ReadUInt32(head, 4, little);
        var fieldsLength = Header.ReadUInt32(head, 12, little);
        var message = new byte[Header.Align8(16 + (int)fieldsLength) + (int)bodyLength];
        head.CopyTo(message, 0);
        await ReadExactlyAsync(stream, message.AsMemory(16));
        return message;
    }

    // -------------------------------------------------- org.freedesktop.DBus

    static byte[] BusReply(Header header, string self, string peer) =>
        header.Member switch {
            "Hello" => Header.MethodReturn(header.Serial, "s", body => Header.AppendString(body, self)),
            "GetNameOwner" or "GetConnectionUnixProcessID" =>
                Header.MethodReturn(header.Serial, "s", body => Header.AppendString(body, peer)),
            "NameHasOwner" => Header.MethodReturn(header.Serial, "b", body => Header.AppendUInt32(body, 1)),
            "RequestName" => Header.MethodReturn(header.Serial, "u", body => Header.AppendUInt32(body, 1)),
            // AddMatch, RemoveMatch and anything else the transport asks for on its own
            // initiative: an empty success. ⚠ A real daemon validates the match rule; this
            // one does not, so a malformed rule passes here and fails on a device.
            _ => Header.MethodReturn(header.Serial, null, null)
        };
}

/// <summary>Hands a <see cref="DBusConnection" /> a stream that is already a bus.</summary>
sealed class StreamOptions : DBusConnectionOptions {
    readonly Stream _stream;

    public StreamOptions(Stream stream) {
        _stream = stream;
    }

    protected override ValueTask<SetupResult> SetupAsync(CancellationToken cancellationToken) =>
        new(new SetupResult {
            ConnectionStream = _stream,
            UserId = "0",
            MachineId = "0123456789abcdef0123456789abcdef"
        });
}

/// <summary>One stream to read from, another to write to.</summary>
/// <remarks>
///     A <see cref="Pipe" /> is one-directional and a bus connection is not, so each end of
///     the loopback is two pipes crossed over. ⚠ Every write flushes: a
///     <see cref="PipeWriter" /> buffers, and an unflushed D-Bus method call is a test that
///     hangs rather than one that fails.
/// </remarks>
sealed class DuplexStream : Stream {
    readonly Stream _read;
    readonly Stream _write;

    public DuplexStream(Stream read, Stream write) {
        _read = read;
        _write = write;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _write.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _read.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) {
        _write.Write(buffer, offset, count);
        _write.Flush();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) {
        await _write.WriteAsync(buffer, cancellationToken);
        await _write.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _read.Dispose();
            _write.Dispose();
        }

        base.Dispose(disposing);
    }
}

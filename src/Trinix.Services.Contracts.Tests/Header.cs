using System.Buffers.Binary;
using System.Text;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     Just enough of the D-Bus message header for <see cref="LoopbackBus" /> to do a
///     daemon's job.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ This is the one place in the repository that parses the wire format by hand,
///         and it exists only because a relay has to read the header of a message it is not
///         the recipient of. Nothing in <c>Trinix.Services.Contracts</c> does this — that is
///         <c>Tmds.DBus.Protocol</c>'s job, and duplicating it in shipping code would be a
///         second implementation of exactly the kind doc 00 forbids.
///     </para>
///     <para>
///         The layout, for whoever has to touch it: twelve fixed bytes (endianness, type,
///         flags, protocol version, body length, serial), then a <c>a(yv)</c> array of
///         header fields introduced by its own byte length at offset 12, then padding to the
///         next multiple of eight, then the body. Header fields are 8-aligned relative to
///         the start of the array, which is itself at offset 16 and therefore already
///         aligned — which is why <see cref="WithSender" /> can rebuild the array and then
///         append the original body bytes unchanged.
///     </para>
/// </remarks>
readonly struct Header {
    const byte FieldInterface = 2;
    const byte FieldMember = 3;
    const byte FieldReplySerial = 5;
    const byte FieldDestination = 6;
    const byte FieldSender = 7;
    const byte FieldSignature = 8;

    Header(uint serial, int fieldsLength, string? member, string? destination) {
        Serial = serial;
        FieldsLength = fieldsLength;
        Member = member;
        Destination = destination;
    }

    /// <summary>The sender's serial, which a reply must quote back.</summary>
    public uint Serial { get; }

    /// <summary>The byte length of the header-field array.</summary>
    public int FieldsLength { get; }

    /// <summary>The member name, when there is one.</summary>
    public string? Member { get; }

    /// <summary>The destination, which is how the relay spots a call to the bus itself.</summary>
    public string? Destination { get; }

    /// <summary>Round a byte offset up to the next multiple of eight.</summary>
    public static int Align8(int value) => (value + 7) & ~7;

    /// <summary>Read a 32-bit unsigned integer in the message's own endianness.</summary>
    public static uint ReadUInt32(byte[] message, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(offset, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset, 4));

    /// <summary>Parse what the relay needs out of a whole message.</summary>
    public static Header Parse(byte[] message) {
        var little = message[0] == (byte)'l';
        var serial = ReadUInt32(message, 8, little);
        var fieldsLength = (int)ReadUInt32(message, 12, little);
        string? member = null;
        string? destination = null;

        foreach (var field in Fields(message, fieldsLength, little)) {
            switch (field.Code) {
                case FieldMember:
                    member = field.Value;
                    break;
                case FieldDestination:
                    destination = field.Value;
                    break;
                default:
                    break;
            }
        }

        return new Header(serial, fieldsLength, member, destination);
    }

    /// <summary>
    ///     The same message with a <c>SENDER</c> field, replacing one if it was already
    ///     there.
    /// </summary>
    public static byte[] WithSender(byte[] message, Header header, string sender) {
        var little = message[0] == (byte)'l';
        var fields = new List<byte>();

        foreach (var field in Fields(message, header.FieldsLength, little)) {
            if (field.Code == FieldSender) {
                continue;
            }

            AppendField(fields, field.Code, field.Signature, field.Value, field.Number, little);
        }

        AppendField(fields, FieldSender, "s", sender, 0, little);

        var bodyStart = Align8(16 + header.FieldsLength);
        var bodyLength = (int)ReadUInt32(message, 4, little);
        var rebuilt = new List<byte>(message.Length + 32);
        rebuilt.AddRange(message.Take(12));
        AppendUInt32At(rebuilt, (uint)fields.Count, little);
        rebuilt.AddRange(fields);
        while (rebuilt.Count % 8 != 0) {
            rebuilt.Add(0);
        }

        rebuilt.AddRange(message.Skip(bodyStart).Take(bodyLength));
        return [.. rebuilt];
    }

    /// <summary>Build a method-return message from the bus itself.</summary>
    public static byte[] MethodReturn(uint replySerial, string? signature, Action<List<byte>>? body) {
        var payload = new List<byte>();
        body?.Invoke(payload);

        var fields = new List<byte>();
        AppendField(fields, FieldReplySerial, "u", null, replySerial, little: true);
        AppendField(fields, FieldSender, "s", "org.freedesktop.DBus", 0, little: true);
        if (signature is not null) {
            AppendField(fields, FieldSignature, "g", signature, 0, little: true);
        }

        var message = new List<byte> { (byte)'l', 2, 1, 1 };
        AppendUInt32At(message, (uint)payload.Count, little: true);
        // ⚠ Any serial the peer has not used. The relay never waits for a reply to its own
        // messages, so uniqueness is all this has to provide.
        AppendUInt32At(message, 0xF0000000 | (replySerial & 0x0FFFFFFF), little: true);
        AppendUInt32At(message, (uint)fields.Count, little: true);
        message.AddRange(fields);
        while (message.Count % 8 != 0) {
            message.Add(0);
        }

        message.AddRange(payload);
        return [.. message];
    }

    /// <summary>Append a D-Bus string to a body under construction.</summary>
    public static void AppendString(List<byte> body, string value) {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendUInt32(body, (uint)utf8.Length);
        body.AddRange(utf8);
        body.Add(0);
    }

    /// <summary>Append a 4-aligned 32-bit unsigned integer to a body under construction.</summary>
    public static void AppendUInt32(List<byte> body, uint value) {
        while (body.Count % 4 != 0) {
            body.Add(0);
        }

        AppendUInt32At(body, value, little: true);
    }

    readonly record struct Field(byte Code, string Signature, string? Value, uint Number);

    static IEnumerable<Field> Fields(byte[] message, int fieldsLength, bool little) {
        var position = 16;
        var end = 16 + fieldsLength;
        while (position < end) {
            position = Align8(position);
            if (position >= end) {
                yield break;
            }

            var code = message[position++];
            var signatureLength = message[position++];
            var signature = Encoding.ASCII.GetString(message, position, signatureLength);
            position += signatureLength + 1;

            string? value = null;
            uint number = 0;
            switch (signature) {
                case "s":
                case "o": {
                    position = (position + 3) & ~3;
                    var length = (int)ReadUInt32(message, position, little);
                    position += 4;
                    value = Encoding.UTF8.GetString(message, position, length);
                    position += length + 1;
                    break;
                }

                case "g": {
                    var length = message[position++];
                    value = Encoding.ASCII.GetString(message, position, length);
                    position += length + 1;
                    break;
                }

                case "u": {
                    position = (position + 3) & ~3;
                    number = ReadUInt32(message, position, little);
                    position += 4;
                    break;
                }

                default:
                    throw new InvalidDataException(
                        $"header field {code} has signature '{signature}', which this relay does not decode. "
                        + "A real daemon would; add it here if a contract starts using it."
                    );
            }

            yield return new Field(code, signature, value, number);
        }
    }

    static void AppendField(List<byte> fields, byte code, string signature, string? value, uint number, bool little) {
        while (fields.Count % 8 != 0) {
            fields.Add(0);
        }

        fields.Add(code);
        fields.Add((byte)signature.Length);
        fields.AddRange(Encoding.ASCII.GetBytes(signature));
        fields.Add(0);

        switch (signature) {
            case "s":
            case "o": {
                while (fields.Count % 4 != 0) {
                    fields.Add(0);
                }

                var utf8 = Encoding.UTF8.GetBytes(value ?? "");
                AppendUInt32At(fields, (uint)utf8.Length, little);
                fields.AddRange(utf8);
                fields.Add(0);
                break;
            }

            case "g": {
                var ascii = Encoding.ASCII.GetBytes(value ?? "");
                fields.Add((byte)ascii.Length);
                fields.AddRange(ascii);
                fields.Add(0);
                break;
            }

            case "u": {
                while (fields.Count % 4 != 0) {
                    fields.Add(0);
                }

                AppendUInt32At(fields, number, little);
                break;
            }

            default:
                throw new InvalidDataException($"cannot re-emit a header field of signature '{signature}'.");
        }
    }

    static void AppendUInt32At(List<byte> buffer, uint value, bool little) {
        var scratch = new byte[4];
        if (little) {
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        } else {
            BinaryPrimitives.WriteUInt32BigEndian(scratch, value);
        }

        buffer.AddRange(scratch);
    }
}

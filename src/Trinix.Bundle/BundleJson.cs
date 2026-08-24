using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Trinix.Bundle;

/// <summary>
///     The serialiser contract for every document Trinix's app format defines.
/// </summary>
/// <remarks>
///     <para>
///         Source-generated rather than reflective, and that is a security property
///         rather than a performance one. The reflective serialiser can be steered by
///         the shape of the input; a generated one can only produce the types listed
///         here. It also means this assembly survives trimming and NativeAOT unchanged,
///         which matters because the launcher is the component most deserving of AOT's
///         startup profile.
///     </para>
///     <para>
///         Indented output is on because these files are meant to be read
///         by people — a signature that nobody can inspect invites being trusted
///         blindly. It costs nothing: the bytes written are the bytes signed, so
///         formatting is not a canonicalisation question.
///     </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
)]
[JsonSerializable(typeof(BundleInfo))]
[JsonSerializable(typeof(BundleManifest))]
[JsonSerializable(typeof(InstallReceipt))]
public sealed partial class BundleJson : JsonSerializerContext {
    /// <summary>
    ///     Serialise to the exact bytes that go on disk, with a trailing newline.
    /// </summary>
    /// <remarks>
    ///     The newline is not decoration. These files are read back and signed by
    ///     byte, and a file that does not end in one is a file that <c>cat</c>
    ///     mangles and that some editors silently fix — which would break the
    ///     signature of a bundle nobody knowingly touched.
    /// </remarks>
    public static byte[] ToBytes<T>(T value, JsonTypeInfo<T> typeInfo) {
        ArgumentNullException.ThrowIfNull(typeInfo);

        var json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var withNewline = new byte[json.Length + 1];
        json.CopyTo(withNewline, 0);
        withNewline[^1] = (byte)'\n';
        return withNewline;
    }
}

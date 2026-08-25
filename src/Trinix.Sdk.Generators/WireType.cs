using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Trinix.Sdk.Generators;

/// <summary>
///     One C# type, resolved to how it crosses the bus.
/// </summary>
/// <remarks>
///     <para>
///         This class is the whole type system of the service layer, and it is deliberately
///         small. Every type Trinix's contracts may use is enumerated in
///         <see cref="Resolve" />; anything else is a diagnostic at compile time rather
///         than a marshaller that does something plausible at run time. That choice is the
///         same one doc 01 makes about reflection: a binding that can be surprised by a
///         type is a binding whose failure arrives in somebody else's build.
///     </para>
///     <para>
///         ⚠ <b>Nothing here allocates a <c>MessageWriter</c> or a <c>Reader</c>.</b> Both
///         are <c>ref struct</c>s, so the code this class emits is always statements inside
///         a method that already has one in a local named <c>writer</c> or <c>reader</c>,
///         and the emitters below take it for granted. That is the constraint doc 01
///         § How the proxies are generated names as the reason the proxies are generated at
///         all, and it is confined to this file and <see cref="ServiceEmitter" />.
///     </para>
/// </remarks>
abstract class WireType {
    /// <summary>The D-Bus signature fragment for this type.</summary>
    public abstract string Signature { get; }

    /// <summary>The fully-qualified C# type to declare a local of.</summary>
    public abstract string CSharpType { get; }

    /// <summary>
    ///     The <c>DBusType</c> member naming this type's <i>alignment</i>, for the one
    ///     place the wire format needs it: the element type handed to
    ///     <c>WriteArrayStart</c> and <c>ReadArrayStart</c>. An empty array still has to be
    ///     padded as if its first element were there.
    /// </summary>
    public abstract string DBusTypeMember { get; }

    /// <summary>Emit statements that write <paramref name="value" /> into <c>writer</c>.</summary>
    public abstract void EmitWrite(CodeWriter code, string value);

    /// <summary>
    ///     Emit statements that read one value out of <c>reader</c> and leave it in a new
    ///     local called <paramref name="local" />.
    /// </summary>
    /// <remarks>
    ///     A local rather than an expression because arrays need a loop, and because
    ///     reading several arguments in order is only correct if each one's statements have
    ///     finished before the next begins. An expression tree would leave that to C#'s
    ///     evaluation order, which is defined but is not the sort of thing a wire format
    ///     should lean on.
    /// </remarks>
    public abstract void EmitRead(CodeWriter code, string local);

    /// <summary>
    ///     Work out how a C# type crosses the bus, or say why it cannot.
    /// </summary>
    /// <param name="type">The declared type, with its nullable annotation.</param>
    /// <param name="known">The <c>[ServiceRecord]</c> types seen so far on this path, to catch a cycle.</param>
    /// <param name="error">Why not, when the result is <see langword="null" />.</param>
    public static WireType? Resolve(ITypeSymbol type, Stack<string> known, out string? error) {
        error = null;

        // A record that is allowed to be absent. D-Bus has no "maybe", and the encoding
        // every protocol on it converges on is an array that holds zero or one element —
        // which is what doc 02's `reply?` needs and what GVariant would have given for
        // free. ⚠ Restricted to [ServiceRecord] types on purpose: a `string?` must not
        // silently become an array of strings, so it is treated as a plain string below
        // and its null is written as the empty string.
        if (type.NullableAnnotation == NullableAnnotation.Annotated
            && type.IsReferenceType
            && HasServiceRecordAttribute(type)) {
            var inner = Resolve(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), known, out error);
            return inner is null ? null : new OptionalWire(inner);
        }

        if (type is IArrayTypeSymbol array) {
            var element = Resolve(array.ElementType, known, out error);
            return element is null ? null : new ArrayWire(element, array.ElementType.ToDisplayString(Fqn), asArray: true);
        }

        if (type is INamedTypeSymbol { IsGenericType: true } generic) {
            var definition = generic.ConstructedFrom.ToDisplayString(Fqn);
            if (SequenceTypes.Contains(definition)) {
                var elementType = generic.TypeArguments[0];
                var element = Resolve(elementType, known, out error);
                return element is null
                    ? null
                    : new ArrayWire(element, elementType.ToDisplayString(Fqn), asArray: false);
            }
        }

        var name = type.ToDisplayString(Fqn);

        if (Primitives.TryGetValue(name, out var primitive)) {
            return primitive;
        }

        if (name == "global::System.DateTimeOffset") {
            return TimeWire.Instant;
        }

        if (name == "global::System.TimeSpan") {
            return TimeWire.Duration;
        }

        if (name == "global::Tmds.DBus.Protocol.ObjectPath") {
            return ObjectPathWire.Instance;
        }

        if (IsSafeHandle(type)) {
            return new HandleWire(name);
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) {
            var basis = Resolve(underlying, known, out error);
            return basis is PrimitiveWire numeric ? new EnumWire(name, numeric) : null;
        }

        if (HasServiceRecordAttribute(type) && type is INamedTypeSymbol record) {
            return ResolveRecord(record, known, out error);
        }

        error = $"'{type.ToDisplayString()}' has no D-Bus representation. "
            + "Use a primitive, an enum, a string, DateTimeOffset, TimeSpan, a SafeHandle, "
            + "a [ServiceRecord] record, or an IReadOnlyList of one of those.";
        return null;
    }

    static RecordWire? ResolveRecord(INamedTypeSymbol record, Stack<string> known, out string? error) {
        error = null;
        var name = record.ToDisplayString(Fqn);

        if (known.Contains(name)) {
            error = $"'{record.Name}' contains itself, directly or through another record. "
                + "A D-Bus structure has a fixed shape and cannot be recursive.";
            return null;
        }

        var constructor = PrimaryConstructor(record);
        if (constructor is null) {
            error = $"'{record.Name}' is marked [ServiceRecord] but has no positional constructor. "
                + "The wire format is its primary constructor's parameters, in order.";
            return null;
        }

        known.Push(name);
        try {
            var members = new List<RecordMember>();
            foreach (var parameter in constructor.Parameters) {
                var member = Resolve(parameter.Type, known, out error);
                if (member is null) {
                    error = $"'{record.Name}.{parameter.Name}': {error}";
                    return null;
                }

                members.Add(new RecordMember(Capitalise(parameter.Name), parameter.Name, member));
            }

            return new RecordWire(record.Name, name, members);
        } finally {
            known.Pop();
        }
    }

    /// <summary>The constructor whose parameters are the wire format.</summary>
    /// <remarks>
    ///     ⚠ A record has more constructors than it looks like it has: the compiler adds a
    ///     copy constructor, and a <c>record struct</c> also gets a parameterless one. Both
    ///     are excluded here, and a record with a second hand-written constructor is
    ///     rejected rather than guessed at — picking "the one with the most parameters"
    ///     would make adding a convenience overload silently change the wire format.
    /// </remarks>
    internal static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol record) {
        var candidates = record.InstanceConstructors
            .Where(c => c.Parameters.Length > 0 && c.DeclaredAccessibility == Accessibility.Public)
            .Where(c => !(c.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, record)))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    internal static bool HasServiceRecordAttribute(ITypeSymbol type) =>
        type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "Trinix.Services.Contracts.ServiceRecordAttribute");

    static bool IsSafeHandle(ITypeSymbol type) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (current.ToDisplayString() == "System.Runtime.InteropServices.SafeHandle") {
                return true;
            }
        }

        return false;
    }

    static string Capitalise(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);

    /// <summary>Fully qualified, with <c>global::</c>, and without nullable annotations.</summary>
    internal static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat;

    static readonly HashSet<string> SequenceTypes = new(System.StringComparer.Ordinal) {
        "global::System.Collections.Generic.IReadOnlyList<T>",
        "global::System.Collections.Generic.IReadOnlyCollection<T>",
        "global::System.Collections.Generic.IEnumerable<T>",
        "global::System.Collections.Generic.IList<T>",
        "global::System.Collections.Generic.List<T>"
    };

    static readonly Dictionary<string, PrimitiveWire> Primitives = new(System.StringComparer.Ordinal) {
        ["bool"] = new PrimitiveWire("b", "bool", "WriteBool", "ReadBool", "Bool"),
        ["byte"] = new PrimitiveWire("y", "byte", "WriteByte", "ReadByte", "Byte"),
        ["short"] = new PrimitiveWire("n", "short", "WriteInt16", "ReadInt16", "Int16"),
        ["ushort"] = new PrimitiveWire("q", "ushort", "WriteUInt16", "ReadUInt16", "UInt16"),
        ["int"] = new PrimitiveWire("i", "int", "WriteInt32", "ReadInt32", "Int32"),
        ["uint"] = new PrimitiveWire("u", "uint", "WriteUInt32", "ReadUInt32", "UInt32"),
        ["long"] = new PrimitiveWire("x", "long", "WriteInt64", "ReadInt64", "Int64"),
        ["ulong"] = new PrimitiveWire("t", "ulong", "WriteUInt64", "ReadUInt64", "UInt64"),
        ["double"] = new PrimitiveWire("d", "double", "WriteDouble", "ReadDouble", "Double"),
        ["string"] = new PrimitiveWire("s", "string", "WriteString", "ReadString", "String")
    };
}

/// <summary>One member of a <see cref="RecordWire" />.</summary>
sealed class RecordMember {
    public RecordMember(string property, string parameter, WireType type) {
        Property = property;
        Parameter = parameter;
        Type = type;
    }

    /// <summary>The property to read the value out of when writing.</summary>
    public string Property { get; }

    /// <summary>The constructor parameter name, used for the local when reading.</summary>
    public string Parameter { get; }

    /// <summary>How it crosses the bus.</summary>
    public WireType Type { get; }
}

/// <summary>A fixed-width scalar or a string: one call in each direction.</summary>
sealed class PrimitiveWire : WireType {
    readonly string _write;
    readonly string _read;

    public PrimitiveWire(string signature, string csharp, string write, string read, string dbusType) {
        Signature = signature;
        CSharpType = csharp;
        _write = write;
        _read = read;
        DBusTypeMember = dbusType;
    }

    public override string Signature { get; }
    public override string CSharpType { get; }
    public override string DBusTypeMember { get; }

    public override void EmitWrite(CodeWriter code, string value) {
        // ⚠ A `string?` reaches here as a plain string: D-Bus has no null string, and the
        // empty string is the only value every reader on the far side already handles.
        // Coalescing is cheap and unconditional rather than conditional on the annotation,
        // because a non-null-annotated reference can still be null at run time and a
        // NullReferenceException inside a MessageWriter aborts the whole connection.
        code.Line(Signature == "s"
            ? $"writer.{_write}({value} ?? string.Empty);"
            : $"writer.{_write}({value});");
    }

    public override void EmitRead(CodeWriter code, string local) => code.Line($"var {local} = reader.{_read}();");
}

/// <summary>An enum, on the wire as whatever it is declared over.</summary>
sealed class EnumWire : WireType {
    readonly PrimitiveWire _basis;

    public EnumWire(string csharp, PrimitiveWire basis) {
        CSharpType = csharp;
        _basis = basis;
    }

    public override string Signature => _basis.Signature;
    public override string CSharpType { get; }
    public override string DBusTypeMember => _basis.DBusTypeMember;

    // ⚠ No range check on the way in. A value the enum does not define is what an
    // application built against a newer contract sends, and refusing it here would turn
    // "the shell added an urgency" into a connection-level failure in every application
    // that has not been rebuilt. The service decides what to do with an unknown member,
    // because only the service knows whether there is a safe default.
    public override void EmitWrite(CodeWriter code, string value) =>
        _basis.EmitWrite(code, $"({_basis.CSharpType}){value}");

    public override void EmitRead(CodeWriter code, string local) {
        var raw = code.NextName("raw");
        _basis.EmitRead(code, raw);
        code.Line($"var {local} = ({CSharpType}){raw};");
    }
}

/// <summary>An object path.</summary>
sealed class ObjectPathWire : WireType {
    public static readonly ObjectPathWire Instance = new();

    ObjectPathWire() { }

    public override string Signature => "o";
    public override string CSharpType => "global::Tmds.DBus.Protocol.ObjectPath";
    public override string DBusTypeMember => "ObjectPath";

    public override void EmitWrite(CodeWriter code, string value) => code.Line($"writer.WriteObjectPath({value});");

    public override void EmitRead(CodeWriter code, string local) =>
        code.Line($"var {local} = reader.ReadObjectPath();");
}

/// <summary>
///     A moment or a duration, both as milliseconds in an <c>x</c>.
/// </summary>
/// <remarks>
///     <para>
///         Milliseconds since the Unix epoch, UTC, in a signed 64-bit integer: the same
///         unit on both sides of the boundary, chosen because it is the one every language
///         that will ever read a Trinix introspection XML already has. ⚠ Not
///         <c>DateTime</c> — a <c>DateTime</c> has a <c>Kind</c> that does not survive the
///         wire, so a local time would arrive as UTC and be wrong by the offset, silently,
///         only for users who are not in UTC.
///     </para>
///     <para>
///         ⚠ Milliseconds, not ticks, so precision is lost. That is deliberate: a
///         notification's timestamp orders a list, and a clipboard's does not exist. Any
///         service that needs better resolution than a millisecond is not a desktop service
///         and should carry its own <c>long</c>.
///     </para>
/// </remarks>
sealed class TimeWire : WireType {
    public static readonly TimeWire Instant = new(true);
    public static readonly TimeWire Duration = new(false);

    readonly bool _absolute;

    TimeWire(bool absolute) {
        _absolute = absolute;
    }

    public override string Signature => "x";
    public override string CSharpType => _absolute ? "global::System.DateTimeOffset" : "global::System.TimeSpan";
    public override string DBusTypeMember => "Int64";

    public override void EmitWrite(CodeWriter code, string value) =>
        code.Line(_absolute
            ? $"writer.WriteInt64({value}.ToUnixTimeMilliseconds());"
            : $"writer.WriteInt64((long){value}.TotalMilliseconds);");

    public override void EmitRead(CodeWriter code, string local) {
        var raw = code.NextName("raw");
        code.Line($"var {raw} = reader.ReadInt64();");
        code.Line(_absolute
            ? $"var {local} = global::System.DateTimeOffset.FromUnixTimeMilliseconds({raw});"
            : $"var {local} = global::System.TimeSpan.FromMilliseconds({raw});");
    }
}

/// <summary>
///     A file descriptor.
/// </summary>
/// <remarks>
///     The <c>h</c> type, and the one the R4 spike went out of its way to prove in both
///     directions including a live socket, because doc 02 § Devices hands over pipes and
///     sockets rather than files. ⚠ The descriptor's <i>ownership</i> is not on the wire:
///     the sender's <c>SafeHandle</c> is duplicated into the message and the receiver's is
///     a new one it must dispose. A service that forgets is a service that runs out of
///     descriptors after a week of uptime, which is the shape of bug that never reproduces
///     on a developer's machine.
/// </remarks>
sealed class HandleWire : WireType {
    public HandleWire(string csharp) {
        CSharpType = csharp;
    }

    public override string Signature => "h";
    public override string CSharpType { get; }
    public override string DBusTypeMember => "UnixFd";

    public override void EmitWrite(CodeWriter code, string value) => code.Line($"writer.WriteHandle({value});");

    public override void EmitRead(CodeWriter code, string local) =>
        code.Line($"var {local} = reader.ReadHandle<{CSharpType}>();");
}

/// <summary>A <c>[ServiceRecord]</c> record, as a D-Bus structure.</summary>
sealed class RecordWire : WireType {
    public RecordWire(string name, string csharp, IReadOnlyList<RecordMember> members) {
        Name = name;
        CSharpType = csharp;
        Members = members;
    }

    /// <summary>The record's short name, which is the suffix of its helper methods.</summary>
    public string Name { get; }

    /// <summary>The members, in wire order.</summary>
    public IReadOnlyList<RecordMember> Members { get; }

    public override string Signature => "(" + string.Concat(Members.Select(m => m.Type.Signature)) + ")";
    public override string CSharpType { get; }
    public override string DBusTypeMember => "Struct";

    // The helpers live in one generated class rather than being inlined at every use, so
    // that a record used by four members marshals through one piece of code. Inlining
    // would make a bug in one nested read appear four times with four different local
    // names, which is exactly as much fun as it sounds.
    public override void EmitWrite(CodeWriter code, string value) =>
        code.Line($"Write{Name}(ref writer, {value});");

    public override void EmitRead(CodeWriter code, string local) =>
        code.Line($"var {local} = Read{Name}(ref reader);");

    /// <summary>Emit the body of the shared writer for this record.</summary>
    public void EmitWriterBody(CodeWriter code) {
        code.Line("writer.WriteStructureStart();");
        foreach (var member in Members) {
            member.Type.EmitWrite(code, "value." + member.Property);
        }
    }

    /// <summary>Emit the body of the shared reader for this record.</summary>
    public void EmitReaderBody(CodeWriter code) {
        // ⚠ Structures are 8-aligned and the alignment is not implied by the members. A
        // reader that skips this is correct for exactly the structures whose first member
        // is already 8-aligned, which is the subset that makes the bug survive testing.
        code.Line("reader.AlignStruct();");
        var locals = new List<string>();
        foreach (var member in Members) {
            var local = code.NextName(member.Parameter + "_");
            member.Type.EmitRead(code, local);
            locals.Add(local);
        }

        code.Line($"return new {CSharpType}({string.Join(", ", locals)});");
    }
}

/// <summary>A sequence, as a D-Bus array.</summary>
sealed class ArrayWire : WireType {
    readonly WireType _element;
    readonly string _elementCSharpType;
    readonly bool _asArray;

    public ArrayWire(WireType element, string elementCSharpType, bool asArray) {
        _element = element;
        _elementCSharpType = elementCSharpType;
        _asArray = asArray;
    }

    public override string Signature => "a" + _element.Signature;

    public override string CSharpType => _asArray
        ? _elementCSharpType + "[]"
        : $"global::System.Collections.Generic.IReadOnlyList<{_elementCSharpType}>";

    public override string DBusTypeMember => "Array";

    public override void EmitWrite(CodeWriter code, string value) {
        var start = code.NextName("arrayStart");
        var item = code.NextName("item");
        code.OpenBrace("");
        code.Line($"var {start} = writer.WriteArrayStart(global::Tmds.DBus.Protocol.DBusType.{_element.DBusTypeMember});");
        code.OpenBrace($"foreach (var {item} in {value} ?? global::System.Array.Empty<{_elementCSharpType}>())");
        _element.EmitWrite(code, item);
        code.CloseBrace();
        code.Line($"writer.WriteArrayEnd({start});");
        code.CloseBrace();
    }

    public override void EmitRead(CodeWriter code, string local) {
        var list = code.NextName("list");
        var end = code.NextName("arrayEnd");
        var item = code.NextName("item");
        code.Line($"var {list} = new global::System.Collections.Generic.List<{_elementCSharpType}>();");
        code.Line($"var {end} = reader.ReadArrayStart(global::Tmds.DBus.Protocol.DBusType.{_element.DBusTypeMember});");
        code.OpenBrace($"while (reader.HasNext({end}))");
        _element.EmitRead(code, item);
        code.Line($"{list}.Add({item});");
        code.CloseBrace();
        code.Line(_asArray ? $"var {local} = {list}.ToArray();" : $"var {local} = {list};");
    }
}

/// <summary>
///     A record that may be absent, as an array of zero or one.
/// </summary>
/// <remarks>
///     ⚠ On the wire this is indistinguishable from a genuine array — the signature is the
///     same <c>a(…)</c> — so a peer that sends two elements is not lying about its type,
///     and the reader here takes the first and drops the rest rather than failing. Refusing
///     would make a forward-compatible extension (a service that one day sends two replies)
///     into a hard error in every application already deployed.
/// </remarks>
sealed class OptionalWire : WireType {
    readonly WireType _element;

    public OptionalWire(WireType element) {
        _element = element;
    }

    public override string Signature => "a" + _element.Signature;
    public override string CSharpType => _element.CSharpType + "?";
    public override string DBusTypeMember => "Array";

    public override void EmitWrite(CodeWriter code, string value) {
        var start = code.NextName("arrayStart");
        code.OpenBrace("");
        code.Line($"var {start} = writer.WriteArrayStart(global::Tmds.DBus.Protocol.DBusType.{_element.DBusTypeMember});");
        code.OpenBrace($"if ({value} is not null)");
        _element.EmitWrite(code, value);
        code.CloseBrace();
        code.Line($"writer.WriteArrayEnd({start});");
        code.CloseBrace();
    }

    public override void EmitRead(CodeWriter code, string local) {
        var end = code.NextName("arrayEnd");
        var item = code.NextName("item");
        code.Line($"{_element.CSharpType}? {local} = null;");
        code.Line($"var {end} = reader.ReadArrayStart(global::Tmds.DBus.Protocol.DBusType.{_element.DBusTypeMember});");
        code.OpenBrace($"while (reader.HasNext({end}))");
        _element.EmitRead(code, item);
        code.Line($"{local} ??= {item};");
        code.CloseBrace();
    }
}

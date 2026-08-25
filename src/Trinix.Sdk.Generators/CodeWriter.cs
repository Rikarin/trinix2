using System.Text;

namespace Trinix.Sdk.Generators;

/// <summary>
///     A <see cref="StringBuilder" /> that knows how deep it is.
/// </summary>
/// <remarks>
///     Generated C# is read by people — in a debugger, in a stack trace, and by whoever is
///     working out why the wire format changed — so it is indented rather than emitted flat
///     and left to a formatter that never runs on it. <c>dotnet format</c> does not see
///     source-generator output: it formats files on disk, and this text never is one.
///     <para>
///         <see cref="NextName" /> is here rather than in the emitter because the
///         marshalling code is recursive — an array of records containing an array — and
///         every nesting level needs its own loop variable. A counter local to one emit
///         call would collide with itself two levels down.
///     </para>
/// </remarks>
sealed class CodeWriter {
    readonly StringBuilder _builder = new();
    int _indent;
    int _names;

    /// <summary>Write one line at the current depth.</summary>
    public void Line(string text = "") {
        if (text.Length > 0) {
            _builder.Append(' ', _indent * 4);
            _builder.Append(text);
        }

        _builder.Append('\n');
    }

    /// <summary>Open a brace and indent.</summary>
    public void OpenBrace(string text) {
        Line(text.Length == 0 ? "{" : text + " {");
        _indent++;
    }

    /// <summary>Indent without a brace — for a wrapped argument list.</summary>
    public void Indent() => _indent++;

    /// <summary>Undo <see cref="Indent" />.</summary>
    public void Outdent() => _indent--;

    /// <summary>Close a brace and outdent.</summary>
    public void CloseBrace(string suffix = "") {
        _indent--;
        Line("}" + suffix);
    }

    /// <summary>A local name nothing else in this file will use.</summary>
    public string NextName(string prefix) => prefix + _names++;

    /// <inheritdoc />
    public override string ToString() => _builder.ToString();
}

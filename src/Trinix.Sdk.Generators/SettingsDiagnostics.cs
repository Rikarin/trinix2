using Microsoft.CodeAnalysis;

namespace Trinix.Sdk.Generators;

/// <summary>
///     Everything <see cref="TrinixSettingsGenerator" /> refuses, and why.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § Settings storage contains one sentence that only means something if
///         something enforces it: "A key with no schema is a bug the generator catches."
///         <see cref="MissingSetting" /> is that sentence. The rest are the corollaries —
///         a schema with no default cannot answer a read, a schema with no summary is
///         indistinguishable from application data, and a settable property would be a
///         second way to write that skips the transaction.
///     </para>
///     <para>
///         ⚠ All errors, for the same reason <see cref="ServiceDiagnostics" /> is: a warning
///         is suppressed the first time it fires on a Friday, and the result here is a
///         preference nobody can reset and a Settings pane with a blank row in it.
///     </para>
/// </remarks>
static class SettingsDiagnostics {
    const string Category = "Trinix.Settings";

    static DiagnosticDescriptor Error(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>A <c>[SettingsSchema]</c> that cannot be turned into one.</summary>
    public static readonly DiagnosticDescriptor MalformedSchema = Error(
        "TRX2001",
        "A settings schema declaration is malformed",
        "'{0}': {1}"
    );

    /// <summary>A property with no <c>[Setting]</c>.</summary>
    public static readonly DiagnosticDescriptor MissingSetting = Error(
        "TRX2002",
        "Every key in a settings schema must be declared",
        "'{0}' has no [Setting]. Doc 02: a key with no schema is a bug the generator catches — "
        + "this is that catch. Give it a default and a summary, or move it out of the schema."
    );

    /// <summary>A property whose type cannot be a preference.</summary>
    public static readonly DiagnosticDescriptor UnsupportedType = Error(
        "TRX2003",
        "This type cannot be a preference",
        "'{0}': {1}"
    );

    /// <summary>A <c>[Setting]</c> with no usable <c>Default</c>.</summary>
    public static readonly DiagnosticDescriptor MissingDefault = Error(
        "TRX2004",
        "A setting needs a default of its own type",
        "'{0}': {1} A preference the user has never touched still has to answer, and doc 02's "
        + "'reset to default' has nothing to reset to otherwise."
    );

    /// <summary>A <c>[Setting]</c> with no <c>Summary</c>.</summary>
    public static readonly DiagnosticDescriptor MissingSummary = Error(
        "TRX2005",
        "A setting needs a summary",
        "'{0}' has no Summary. This is not a documentation lapse: doc 02 draws the line between a "
        + "preference and application data at whether it can be described in a schema, and a window "
        + "position or a cached token has no sentence a user would recognise. Doc 08 then builds the "
        + "Settings search index out of these. If you cannot write one, what you have is data, and "
        + "doc 02 says it belongs in the container."
    );

    /// <summary>A member of a settings interface that is not a get-only property.</summary>
    public static readonly DiagnosticDescriptor NotAKey = Error(
        "TRX2006",
        "A settings schema holds get-only properties and nothing else",
        "'{0}' is not a get-only property. A setter would be a second way to write, one that skips "
        + "the transaction and therefore skips both the atomicity doc 02 asks for and the change "
        + "notification; a method would be behaviour, and a schema is a declaration."
    );

    /// <summary>Two schemas in one assembly claiming the same identifier.</summary>
    public static readonly DiagnosticDescriptor DuplicateSchema = Error(
        "TRX2007",
        "Two settings schemas share an identifier",
        "'{0}' is declared by more than one interface in this assembly. A schema identifier "
        + "namespaces a user's stored preferences, so two declarations of it are two applications "
        + "writing each other's keys."
    );
}

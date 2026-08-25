using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Trinix.Sdk.Generators;

/// <summary>
///     Turns one <c>[SettingsSchema]</c> interface into the typed reader, the typed
///     multi-key editor, and the runtime schema the store registers.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § Settings storage asks for settings that are "typed, schema-declared,
///         with a default", and says a key with no schema is a bug the generator catches.
///         The catch is the whole reason this is a generator rather than a convention: the
///         alternative API is <c>store.Get("com.example.app", "AcentColor")</c>, where the
///         typo is not an error anywhere — it is simply a key nobody ever wrote, reading its
///         default forever, in a build that passes.
///     </para>
///     <para>
///         What comes out is deliberately not an implementation of the declared interface.
///         The interface is a declaration and is never implemented by anything, so that
///         there can be no second settings object backed by fields, agreeing with the store
///         until it does not. What comes out is a reader over
///         <c>Trinix.Settings.SettingsStore</c> plus a nested <c>Edit</c> whose setters
///         chain into one transaction — which is how doc 02's atomic multi-key write becomes
///         the shape a caller reaches for first.
///     </para>
///     <para>
///         ⚠ This generator shares <see cref="TrinixServiceGenerator" />'s caching problem
///         and its excuse: symbols are held across the pipeline rather than projected into
///         an equatable model. A handful of schemas per assembly makes that the wrong thing
///         to spend a day on, and the fix is mechanical if an assembly ever grows enough
///         schemas for typing in it to feel slow.
///     </para>
/// </remarks>
[Generator]
public sealed class TrinixSettingsGenerator : IIncrementalGenerator {
    const string SchemaAttribute = "Trinix.Settings.SettingsSchemaAttribute";
    const string SettingAttribute = "Trinix.Settings.SettingAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var schemas = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SchemaAttribute,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol
            )
            .Collect();

        context.RegisterSourceOutput(schemas, static (production, declared) => Emit(production, declared));
    }

    static void Emit(SourceProductionContext production, ImmutableArray<INamedTypeSymbol> declared) {
        if (declared.IsDefaultOrEmpty) {
            return;
        }

        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var symbol in declared.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()) {
            var contract = Resolve(production, symbol);
            if (contract is null) {
                continue;
            }

            // ⚠ Reported rather than mangled. Two interfaces claiming one identifier is two
            // applications writing each other's keys in a user's database, and the second
            // one to register is refused at runtime anyway — better here, where it names
            // both declarations.
            if (!seen.Add(contract.Id)) {
                production.ReportDiagnostic(Diagnostic.Create(
                    SettingsDiagnostics.DuplicateSchema, Where(symbol), contract.Id
                ));
                continue;
            }

            production.AddSource(
                contract.AccessorName + ".Settings.g.cs",
                SourceText.From(SettingsEmitter.Emit(contract), Encoding.UTF8)
            );
        }
    }

    static SettingsContract? Resolve(SourceProductionContext production, INamedTypeSymbol schema) {
        var attribute = schema.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SchemaAttribute);
        if (attribute is null) {
            return null;
        }

        var id = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? ""
            : "";

        var problem =
            !IsWellFormedId(id)
                ? $"'{id}' is not a schema identifier. Reverse-DNS, lowercase, at least two "
                  + "dot-separated segments of [a-z0-9-] — for example com.trinix.desktop.appearance. "
                  + "It is stated rather than derived from the C# namespace so that a rename cannot "
                  + "orphan a user's preferences."
                : schema.Arity > 0
                    ? "a settings schema cannot be generic. There is one database and one set of "
                      + "keys; a schema parameterised by a type would be several."
                    : schema.ContainingType is not null
                        ? "a settings schema cannot be a nested type — the generated accessor is a "
                          + "top-level class in the declaring namespace."
                        : null;

        if (problem is not null) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.MalformedSchema, Where(schema), schema.Name, problem
            ));
            return null;
        }

        var isPublic = schema.DeclaredAccessibility == Accessibility.Public;
        var members = new List<SettingsMember>();
        var failed = false;

        foreach (var member in schema.GetMembers()) {
            if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }) {
                continue;
            }

            if (member is not IPropertySymbol property || property.SetMethod is not null || property.IsIndexer) {
                production.ReportDiagnostic(Diagnostic.Create(
                    SettingsDiagnostics.NotAKey, Where(member), member.Name
                ));
                failed = true;
                continue;
            }

            var resolved = ResolveMember(production, property, isPublic);
            if (resolved is null) {
                failed = true;
            } else {
                members.Add(resolved);
            }
        }

        if (failed) {
            return null;
        }

        if (members.Count == 0) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.MalformedSchema, Where(schema), schema.Name,
                "a schema with no keys declares nothing."
            ));
            return null;
        }

        var containing = schema.ContainingNamespace;
        return new SettingsContract(
            id,
            containing is { IsGlobalNamespace: false } ? containing.ToDisplayString() : "",
            schema.Name,
            TrinixServiceGenerator.ShortName(schema.Name),
            isPublic ? "public" : "internal",
            members
        );
    }

    static SettingsMember? ResolveMember(
        SourceProductionContext production,
        IPropertySymbol property,
        bool isPublic
    ) {
        var setting = property.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SettingAttribute);

        if (setting is null) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.MissingSetting, Where(property), property.Name
            ));
            return null;
        }

        var summary = Named(setting, "Summary");
        if (summary.Kind == TypedConstantKind.Error
            || summary.Value is not string text
            || text.Trim().Length == 0) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.MissingSummary, Where(property), property.Name
            ));
            return null;
        }

        var kind = KindOf(property.Type, out var unsupported);
        if (kind is null) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.UnsupportedType, Where(property), property.Name, unsupported
            ));
            return null;
        }

        // ⚠ Caught here rather than left to the compiler. A public schema whose key is an
        // internal enum produces a public accessor with an internal member type, which is
        // CS0053 — reported against a line of generated source, naming a property nobody
        // wrote, in a file that is not on disk. This message names the declaration instead.
        if (isPublic && property.Type.DeclaredAccessibility != Accessibility.Public
                     && property.Type.SpecialType == SpecialType.None) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.UnsupportedType, Where(property), property.Name,
                $"'{property.Type.ToDisplayString()}' is not public, but the schema that declares this "
                + "key is. The generated accessor inherits the schema's accessibility, so it cannot "
                + "expose a type that is less visible than itself."
            ));
            return null;
        }

        // ⚠ Nullable is refused for every kind, not only for string. "Unset" is already a
        // concept here — it is the schema default, and doc 02 makes resetting to it a
        // feature — so a second one spelled null would be two ways to say the same thing
        // and no way to store the difference.
        if (property.NullableAnnotation == NullableAnnotation.Annotated) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.UnsupportedType, Where(property), property.Name,
                "a preference cannot be nullable. Every key has a default, and a key that is at its "
                + "default is exactly what 'unset' means here — there is no second kind of absence."
            ));
            return null;
        }

        var @default = Named(setting, "Default");
        if (@default.Kind == TypedConstantKind.Error || @default.IsNull) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.MissingDefault, Where(property), property.Name,
                "there is no Default."
            ));
            return null;
        }

        var value = DefaultFor(kind, property.Type, @default, out var wrong, out var literal, out var members);
        if (value is null) {
            production.ReportDiagnostic(Diagnostic.Create(
                SettingsDiagnostics.MissingDefault, Where(property), property.Name, wrong
            ));
            return null;
        }

        return new SettingsMember(
            property.Name,
            kind,
            property.Type.ToDisplayString(WireType.Fqn),
            value,
            literal!,
            text,
            members
        );
    }

    /// <summary>
    ///     Which <c>SettingKind</c> a declared property type is, if any.
    /// </summary>
    /// <remarks>
    ///     ⚠ The list is short because <c>SettingKind</c>'s is: doc 02 says a preference is
    ///     small, user-meaningful and resettable, and every kind here is one scalar a
    ///     Settings pane renders as one control. The refusal messages say so rather than
    ///     saying "unsupported", because whoever hits this is about to look for a workaround
    ///     and the useful answer is where the thing they have actually belongs.
    /// </remarks>
    static string? KindOf(ITypeSymbol type, out string problem) {
        problem = "";

        if (type.TypeKind == TypeKind.Enum) {
            foreach (var attribute in type.GetAttributes()) {
                if (attribute.AttributeClass?.ToDisplayString() == "System.FlagsAttribute") {
                    problem = "a [Flags] enum cannot be a preference. Settings store an enum by member "
                              + "name so that a database is readable on a console and a renumbering "
                              + "cannot silently repoint every user's value, and a combination of flags "
                              + "has no member name. Declare one bool per flag: they are separate "
                              + "preferences to the person setting them anyway.";
                    return null;
                }
            }

            return "Enum";
        }

        switch (type.SpecialType) {
            case SpecialType.System_Boolean:
                return "Bool";
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
                return "Integer";
            case SpecialType.System_Double:
                return "Real";
            case SpecialType.System_String:
                return "Text";
        }

        problem = $"'{type.ToDisplayString()}' is not one of the five things a preference may be "
                  + "(bool, int, long, double, string, or an enum). ⚠ There is deliberately no list, "
                  + "dictionary or blob: doc 02 says the schema is what separates a preference from "
                  + "application data, and a key holding a collection holds records whose fields are "
                  + "in no schema. If this is data, it belongs in the application's container.";
        return null;
    }

    static string? DefaultFor(
        string kind,
        ITypeSymbol type,
        TypedConstant constant,
        out string problem,
        out string? literal,
        out IReadOnlyList<string>? enumMembers
    ) {
        problem = "";
        literal = null;
        enumMembers = null;

        switch (kind) {
            case "Bool":
                if (constant.Value is bool flag) {
                    literal = flag ? "true" : "false";
                    return "global::Trinix.Settings.SettingValue.Bool(" + literal + ")";
                }

                break;

            case "Integer":
                if (constant.Value is int or long) {
                    var number = System.Convert.ToInt64(constant.Value, CultureInfo.InvariantCulture);
                    if (type.SpecialType == SpecialType.System_Int32
                        && (number > int.MaxValue || number < int.MinValue)) {
                        problem = "the Default does not fit in an int.";
                        return null;
                    }

                    literal = number.ToString(CultureInfo.InvariantCulture);
                    return "global::Trinix.Settings.SettingValue.Integer(" + literal + "L)";
                }

                break;

            case "Real":
                if (constant.Value is double or float or int or long) {
                    var real = System.Convert.ToDouble(constant.Value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(real) || double.IsInfinity(real)) {
                        problem = "the Default is not a finite number, and a preference that is NaN "
                                  + "is one no control can render and no reset can restore.";
                        return null;
                    }

                    literal = real.ToString("R", CultureInfo.InvariantCulture) + "D";
                    return "global::Trinix.Settings.SettingValue.Real(" + literal + ")";
                }

                break;

            case "Text":
                if (constant.Value is string s) {
                    literal = Quote(s);
                    return "global::Trinix.Settings.SettingValue.Text(" + literal + ")";
                }

                break;

            case "Enum":
                if (constant.Kind != TypedConstantKind.Enum
                    || !SymbolEqualityComparer.Default.Equals(constant.Type, type)) {
                    break;
                }

                var names = EnumMemberNames(type);
                var name = NameOfConstant(type, constant.Value);
                if (name is null) {
                    problem = "the Default is a value the enum does not define — a cast from an int, "
                              + "most likely. Settings store an enum by member name, and there is no "
                              + "name to store.";
                    return null;
                }

                enumMembers = names;
                literal = type.ToDisplayString(WireType.Fqn) + "." + name;
                return "global::Trinix.Settings.SettingValue.EnumMember(" + Quote(name) + ")";
        }

        problem = $"the Default is not a {type.ToDisplayString()}.";
        return null;
    }

    static List<string> EnumMemberNames(ITypeSymbol type) {
        var names = new List<string>();
        foreach (var member in type.GetMembers()) {
            if (member is IFieldSymbol { HasConstantValue: true, IsStatic: true } field) {
                names.Add(field.Name);
            }
        }

        return names;
    }

    /// <summary>
    ///     The first member with this constant value, or null.
    /// </summary>
    /// <remarks>
    ///     ⚠ "First" matters: an enum may alias one value under two names, and the pair has
    ///     to resolve to the same stored string every time or a value written by one build
    ///     would read back as a different member in the next. Declaration order is stable
    ///     across compilations in a way that nothing else here is.
    /// </remarks>
    static string? NameOfConstant(ITypeSymbol type, object? value) {
        foreach (var member in type.GetMembers()) {
            if (member is IFieldSymbol { HasConstantValue: true, IsStatic: true } field
                && Equals(field.ConstantValue, value)) {
                return field.Name;
            }
        }

        return null;
    }

    static TypedConstant Named(AttributeData attribute, string key) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == key) {
                return argument.Value;
            }
        }

        return default;
    }

    /// <summary>
    ///     A C# string literal for <paramref name="text" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Hand-rolled rather than <c>SymbolDisplay.FormatLiteral</c>, which is available
    ///     but escapes non-ASCII into <c>\uXXXX</c>. Settings summaries are user-facing
    ///     English prose today and will be translated tomorrow, and generated source full of
    ///     escaped code points is source nobody can read in a diff.
    /// </remarks>
    static string Quote(string text) {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (var character in text) {
            switch (character) {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\t': quoted.Append("\\t"); break;
                case '\0': quoted.Append("\\0"); break;
                default: quoted.Append(character); break;
            }
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    ///     The same rule <c>Trinix.Settings.SettingsSchema.IsWellFormedId</c> applies at
    ///     runtime.
    /// </summary>
    /// <remarks>
    ///     ⚠ Duplicated rather than shared, and it has to be: this assembly targets
    ///     netstandard2.0 and is loaded into the compiler, so it cannot reference
    ///     <c>Trinix.Settings</c> — that reference runs the other way. Public rather than
    ///     internal for one reason: the generator tests call it and
    ///     <c>SettingsSchema.IsWellFormedId</c> over the same inputs and assert they agree,
    ///     which is what makes the duplication safe rather than merely unavoidable. Nothing
    ///     references this assembly as a library except that suite.
    /// </remarks>
    /// <param name="id">The candidate identifier.</param>
    public static bool IsWellFormedId(string? id) {
        if (string.IsNullOrEmpty(id) || id![0] == '.' || id[id.Length - 1] == '.') {
            return false;
        }

        var segments = 1;
        var previousWasDot = false;
        foreach (var character in id) {
            if (character == '.') {
                if (previousWasDot) {
                    return false;
                }

                segments++;
                previousWasDot = true;
                continue;
            }

            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) {
                return false;
            }

            previousWasDot = false;
        }

        return segments >= 2;
    }

    static Location Where(ISymbol symbol) =>
        symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;
}

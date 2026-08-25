using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Trinix.Sdk.Generators;

/// <summary>
///     Emits, from one annotated interface, everything the two ends of a Trinix service
///     need in order not to disagree.
/// </summary>
/// <remarks>
///     <para>
///         Doc 01 § How the proxies are generated gives three reasons this is a Roslyn
///         generator and not a reflective binding, and only the first is about speed.
///         Everything in Trinix builds with <c>IsAotCompatible</c> and the trim analyser
///         on; a reflection-based bus binding fails that on the first <c>PublishAot</c>,
///         and the failure appears in the compositor's build rather than in the SDK's.
///         The second is that the C# is the source of truth and the XML is derived, so
///         that the interface is not an artefact of the wire format. The third is the one
///         that decided it: both sides come from one declaration, because a service whose
///         client and server can disagree will.
///     </para>
///     <para>
///         ⚠ <b>This generator holds symbols across the pipeline and therefore caches
///         badly.</b> The alternative — projecting every declaration into an equatable
///         model before emission — is the right shape for a generator that runs on every
///         keystroke in a large solution, and it is not worth it for the handful of
///         interfaces in <c>Trinix.Services.Contracts</c>. If the contracts assembly ever
///         grows to the point where typing in it feels slow, this is the thing to fix, and
///         the fix is mechanical rather than a redesign.
///     </para>
/// </remarks>
[Generator]
public sealed class TrinixServiceGenerator : IIncrementalGenerator {
    const string ServiceAttribute = "Trinix.Services.Contracts.TrinixServiceAttribute";
    const string RecordAttribute = "Trinix.Services.Contracts.ServiceRecordAttribute";
    const string SignalAttribute = "Trinix.Services.Contracts.ServiceSignalAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var wireClass = context.CompilationProvider.Select(
            static (compilation, _) => WireClassName(compilation.AssemblyName)
        );

        var records = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RecordAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol
            )
            .Collect();

        var services = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ServiceAttribute,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol
            )
            .Collect();

        context.RegisterSourceOutput(records.Combine(wireClass), static (production, input) =>
            EmitWire(production, input.Left, input.Right));

        context.RegisterSourceOutput(services.Combine(wireClass), static (production, input) =>
            EmitServices(production, input.Left, input.Right));
    }

    /// <summary>
    ///     Where the shared marshalling helpers go.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named after the assembly rather than being a fixed namespace, because two
    ///     assemblies that both ran this generator and both emitted
    ///     <c>Trinix.Services.Wire.ServiceWire</c> would collide the moment something
    ///     referenced both — and the error would name a type neither developer wrote.
    /// </remarks>
    internal static string WireClassName(string? assemblyName) {
        var builder = new StringBuilder();
        foreach (var character in assemblyName ?? "Trinix") {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' || character == '.'
                ? character
                : '_');
        }

        return builder.Append(".Generated.ServiceWire").ToString();
    }

    static void EmitWire(
        SourceProductionContext production,
        ImmutableArray<INamedTypeSymbol> records,
        string wireClass
    ) {
        if (records.IsDefaultOrEmpty) {
            return;
        }

        var resolved = new List<RecordWire>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var record in records.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()) {
            var wire = WireType.Resolve(record, new Stack<string>(), out var error);
            if (wire is not RecordWire structure) {
                production.ReportDiagnostic(Diagnostic.Create(
                    ServiceDiagnostics.UnsupportedType,
                    Where(record),
                    error ?? $"'{record.Name}' cannot be marshalled as a D-Bus structure."
                ));
                continue;
            }

            // ⚠ Two records with the same short name in different namespaces would emit
            // two Write<Name> helpers into one class. Reporting is better than mangling:
            // a mangled name is a name nobody can search for.
            if (!seen.Add(structure.Name)) {
                production.ReportDiagnostic(Diagnostic.Create(
                    ServiceDiagnostics.UnsupportedType,
                    Where(record),
                    $"another [ServiceRecord] in this assembly is also called '{structure.Name}'. "
                    + "The marshalling helpers are named after the record, so the names must be unique."
                ));
                continue;
            }

            resolved.Add(structure);
        }

        if (resolved.Count == 0) {
            return;
        }

        production.AddSource("ServiceWire.g.cs", SourceText.From(
            WireEmitter.Emit(resolved, wireClass),
            Encoding.UTF8
        ));
    }

    static void EmitServices(
        SourceProductionContext production,
        ImmutableArray<INamedTypeSymbol> services,
        string wireClass
    ) {
        if (services.IsDefaultOrEmpty) {
            return;
        }

        foreach (var service in services.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()) {
            var contract = Resolve(production, service);
            if (contract is null) {
                continue;
            }

            foreach (var file in ServiceEmitter.Emit(contract, wireClass)) {
                production.AddSource(file.HintName, SourceText.From(file.Text, Encoding.UTF8));
            }
        }
    }

    static ServiceContract? Resolve(SourceProductionContext production, INamedTypeSymbol service) {
        var attribute = service.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ServiceAttribute);
        if (attribute is null) {
            return null;
        }

        var interfaceName = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? ""
            : "";
        var busName = Named(attribute, "BusName");
        var objectPath = Named(attribute, "ObjectPath");
        var version = attribute.NamedArguments
            .Where(a => a.Key == "Version")
            .Select(a => a.Value.Value as int? ?? 1)
            .DefaultIfEmpty(1)
            .First();

        var problem = interfaceName.Length == 0
            ? "the D-Bus interface name is empty."
            : busName.Length == 0
                ? "BusName is not set. Doc 02: the interface names the contract, the bus name names the process that serves it, and neither is derivable from the other."
                : objectPath.Length == 0
                    ? "ObjectPath is not set."
                    : null;

        if (problem is not null) {
            production.ReportDiagnostic(Diagnostic.Create(
                ServiceDiagnostics.IncompleteService, Where(service), service.Name, problem
            ));
            return null;
        }

        var methods = new List<ServiceMethod>();
        var signals = new List<ServiceSignal>();
        var failed = false;

        foreach (var member in service.GetMembers()) {
            switch (member) {
                case IPropertySymbol property:
                    production.ReportDiagnostic(Diagnostic.Create(
                        ServiceDiagnostics.NoProperties, Where(property), property.Name
                    ));
                    failed = true;
                    continue;

                case IMethodSymbol { MethodKind: MethodKind.Ordinary } method: {
                    var signal = method.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SignalAttribute);

                    if (signal is not null) {
                        var resolved = ResolveSignal(production, method, signal);
                        if (resolved is null) {
                            failed = true;
                        } else {
                            signals.Add(resolved);
                        }
                    } else {
                        var resolved = ResolveMethod(production, method);
                        if (resolved is null) {
                            failed = true;
                        } else {
                            methods.Add(resolved);
                        }
                    }

                    continue;
                }

                default:
                    continue;
            }
        }

        if (failed) {
            return null;
        }

        var containing = service.ContainingNamespace;
        return new ServiceContract(
            interfaceName,
            busName,
            objectPath,
            version,
            containing is { IsGlobalNamespace: false } ? containing.ToDisplayString() : "",
            service.ToDisplayString(WireType.Fqn),
            ShortName(service.Name),
            methods,
            signals
        );
    }

    static ServiceMethod? ResolveMethod(SourceProductionContext production, IMethodSymbol method) {
        var returnType = method.ReturnType as INamedTypeSymbol;
        var returnName = returnType?.ConstructedFrom.ToDisplayString(WireType.Fqn)
            ?? method.ReturnType.ToDisplayString(WireType.Fqn);

        var isTask = returnName == "global::System.Threading.Tasks.Task";
        var isTaskOf = returnName == "global::System.Threading.Tasks.Task<TResult>";

        if (!isTask && !isTaskOf) {
            production.ReportDiagnostic(Diagnostic.Create(
                ServiceDiagnostics.MustBeAsync, Where(method), method.Name
            ));
            return null;
        }

        // Doc 01: "No IsPermissionGranted boolean." Caught here rather than in review,
        // because the rule is only worth anything if it holds on the day somebody is in a
        // hurry. A Task<bool> whose name mentions a permission is the exact shape.
        if (isTaskOf
            && returnType!.TypeArguments[0].SpecialType == SpecialType.System_Boolean
            && method.Name.IndexOf("Permission", System.StringComparison.Ordinal) >= 0) {
            production.ReportDiagnostic(Diagnostic.Create(
                ServiceDiagnostics.NoPermissionQuery, Where(method), method.Name
            ));
            return null;
        }

        if (method.Parameters.Length == 0
            || method.Parameters[method.Parameters.Length - 1].Type.ToDisplayString(WireType.Fqn)
            != "global::System.Threading.CancellationToken") {
            production.ReportDiagnostic(Diagnostic.Create(
                ServiceDiagnostics.NeedsCancellation, Where(method), method.Name
            ));
            return null;
        }

        var parameters = new List<ServiceParameter>();
        for (var i = 0; i < method.Parameters.Length - 1; i++) {
            var parameter = method.Parameters[i];
            var wire = WireType.Resolve(parameter.Type, new Stack<string>(), out var error);
            if (wire is null) {
                production.ReportDiagnostic(Diagnostic.Create(
                    ServiceDiagnostics.UnsupportedType, Where(parameter),
                    $"'{method.Name}({parameter.Name})': {error}"
                ));
                return null;
            }

            parameters.Add(new ServiceParameter(parameter.Name, wire));
        }

        WireType? result = null;
        if (isTaskOf) {
            result = WireType.Resolve(returnType!.TypeArguments[0], new Stack<string>(), out var error);
            if (result is null) {
                production.ReportDiagnostic(Diagnostic.Create(
                    ServiceDiagnostics.UnsupportedType, Where(method),
                    $"'{method.Name}' returns a value that cannot cross the bus: {error}"
                ));
                return null;
            }
        }

        return new ServiceMethod(method.Name, MemberName(method.Name), parameters, result);
    }

    static ServiceSignal? ResolveSignal(
        SourceProductionContext production,
        IMethodSymbol method,
        AttributeData attribute
    ) {
        var name = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? ""
            : "";

        var wrong = name.Length == 0
            || method.ReturnType is not INamedTypeSymbol returnType
            || returnType.ConstructedFrom.ToDisplayString(WireType.Fqn) != "global::System.Threading.Tasks.Task<TResult>"
            || returnType.TypeArguments[0].ToDisplayString(WireType.Fqn) != "global::System.IDisposable"
            || method.Parameters.Length != 2
            || method.Parameters[1].Type.ToDisplayString(WireType.Fqn) != "global::System.Threading.CancellationToken"
            || method.Parameters[0].Type is not INamedTypeSymbol handler
            || handler.ConstructedFrom.ToDisplayString(WireType.Fqn) != "global::System.Action<T>";

        if (wrong) {
            production.ReportDiagnostic(Diagnostic.Create(
                ServiceDiagnostics.MalformedSignal, Where(method), method.Name
            ));
            return null;
        }

        var payloadType = ((INamedTypeSymbol)method.Parameters[0].Type).TypeArguments[0];
        var payload = WireType.Resolve(payloadType, new Stack<string>(), out var error);
        if (payload is null) {
            production.ReportDiagnostic(Diagnostic.Create(
                ServiceDiagnostics.UnsupportedType, Where(method),
                $"'{method.Name}' carries a payload that cannot cross the bus: {error}"
            ));
            return null;
        }

        return new ServiceSignal(method.Name, name, payload);
    }

    static string Named(AttributeData attribute, string key) => attribute.NamedArguments
        .Where(a => a.Key == key)
        .Select(a => a.Value.Value as string ?? "")
        .DefaultIfEmpty("")
        .First();

    /// <summary>
    ///     The D-Bus member name for a C# method: its name without the <c>Async</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Derived, unlike a signal's name, which is stated. The asymmetry is not an
    ///     oversight: a method is <c>PostAsync</c> and the suffix is the only affix, while
    ///     a signal subscription is <c>WatchActivatedAsync</c> and stripping two affixes is
    ///     a rule that eventually meets <c>WatchAsyncOperationAsync</c> and renames a signal
    ///     nobody then receives.
    /// </remarks>
    internal static string MemberName(string csharpName) =>
        csharpName.Length > "Async".Length && csharpName.EndsWith("Async", System.StringComparison.Ordinal)
            ? csharpName.Substring(0, csharpName.Length - "Async".Length)
            : csharpName;

    /// <summary>The interface's name without its leading <c>I</c>.</summary>
    internal static string ShortName(string interfaceName) =>
        interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1])
            ? interfaceName.Substring(1)
            : interfaceName;

    static Location Where(ISymbol symbol) =>
        symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;
}

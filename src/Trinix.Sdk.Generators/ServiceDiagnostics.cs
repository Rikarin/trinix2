using Microsoft.CodeAnalysis;

namespace Trinix.Sdk.Generators;

/// <summary>
///     Everything the generator refuses to generate, and why.
/// </summary>
/// <remarks>
///     <para>
///         Half of these are not marshalling problems. <see cref="MustBeAsync" />,
///         <see cref="NoProperties" /> and <see cref="NoPermissionQuery" /> encode rules
///         from doc 01 § The service surface that would otherwise be conventions in a
///         document, and a convention in a document is a rule that holds until the first
///         person who has not read it. The generator is the only thing every service
///         contract passes through, so it is where they can be made structural.
///     </para>
///     <para>
///         ⚠ All of them are <see cref="DiagnosticSeverity.Error" />. A warning would be
///         suppressed the first time one fired on a Friday, and a service contract that
///         compiled with the wrong shape would then be a wire format.
///     </para>
/// </remarks>
static class ServiceDiagnostics {
    const string Category = "Trinix.Services";

    static DiagnosticDescriptor Error(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>A member that is not <c>Task</c> or <c>Task&lt;T&gt;</c>.</summary>
    public static readonly DiagnosticDescriptor MustBeAsync = Error(
        "TRX1001",
        "A service member must be asynchronous",
        "'{0}' must return Task or Task<T>. Doc 01: every member of the service surface is "
        + "async even where the implementation is local, because a synchronous call that "
        + "works today becomes synchronous IPC tomorrow with a thousand call sites already "
        + "on the UI thread."
    );

    /// <summary>A member whose last parameter is not a <c>CancellationToken</c>.</summary>
    public static readonly DiagnosticDescriptor NeedsCancellation = Error(
        "TRX1002",
        "A service member must take a CancellationToken last",
        "'{0}' must take a CancellationToken as its last parameter. Every call here crosses "
        + "a process boundary to a service that may be restarting, and a caller with no way "
        + "to stop waiting is a caller that hangs."
    );

    /// <summary>A property on a service interface.</summary>
    public static readonly DiagnosticDescriptor NoProperties = Error(
        "TRX1003",
        "A service interface cannot have properties",
        "'{0}' is a property. A property getter is synchronous by construction, so it cannot "
        + "be a bus call; use a Get…Async method. D-Bus properties exist and are deliberately "
        + "not used: they are an untyped a{{sv}} side-channel with their own change signal, "
        + "which is a second way to say what a method already says."
    );

    /// <summary>A member that asks whether a permission is held.</summary>
    public static readonly DiagnosticDescriptor NoPermissionQuery = Error(
        "TRX1004",
        "A service may not offer a permission query",
        "'{0}' looks like a permission query. Doc 01: there is no IsPermissionGranted "
        + "boolean, because asking teaches applications to branch on authority and produces "
        + "two code paths of which one is never tested. Do the thing and let it throw "
        + "PermissionDeniedException, or wrap the control in a PermissionGate."
    );

    /// <summary>A parameter, return value or record member that cannot be marshalled.</summary>
    public static readonly DiagnosticDescriptor UnsupportedType = Error(
        "TRX1005",
        "This type cannot cross the bus",
        "{0}"
    );

    /// <summary>A <c>[ServiceSignal]</c> method that is not spelled the one way.</summary>
    public static readonly DiagnosticDescriptor MalformedSignal = Error(
        "TRX1006",
        "A signal subscription has one shape",
        "'{0}' must be declared as Task<IDisposable> Watch…Async(Action<TPayload> handler, "
        + "CancellationToken cancellationToken). One payload parameter, always: a signal that "
        + "carries two things carries a [ServiceRecord] with two members, so that adding a "
        + "third changes one type rather than a signature."
    );

    /// <summary>A <c>[TrinixService]</c> attribute missing something it needs.</summary>
    public static readonly DiagnosticDescriptor IncompleteService = Error(
        "TRX1007",
        "A service declaration is incomplete",
        "'{0}': {1}"
    );
}

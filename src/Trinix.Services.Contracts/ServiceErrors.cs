using System.Globalization;
using Tmds.DBus.Protocol;
using Trinix.Bundle;

namespace Trinix.Services.Contracts;

/// <summary>
///     Something went wrong on the far side of a service call.
/// </summary>
/// <remarks>
///     A base class rather than one flat exception, because the three things that can go
///     wrong at this layer want three different sentences from an application: it was
///     refused (<see cref="PermissionDeniedException" />), the service is older than the
///     application (<see cref="ServiceVersionException" />), or the service said no for a
///     reason of its own. Anything below that — a broken socket, a dead daemon — arrives
///     as the transport's own exception and is deliberately not wrapped: a
///     <c>DBusConnectionClosedException</c> that has been rewritten into a Trinix type
///     has lost the only stack that says which connection died.
/// </remarks>
public class TrinixServiceException : Exception {
    /// <summary>Wrap a failure with the D-Bus error name that produced it.</summary>
    /// <param name="errorName">The D-Bus error name, e.g. <c>io.trinix.Error.Unsupported</c>.</param>
    /// <param name="message">What to tell the developer.</param>
    public TrinixServiceException(string errorName, string message) : base(message) {
        ErrorName = errorName;
    }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    public TrinixServiceException() { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    /// <param name="message">What to tell the developer.</param>
    public TrinixServiceException(string message) : base(message) { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    /// <param name="message">What to tell the developer.</param>
    /// <param name="innerException">What caused it.</param>
    public TrinixServiceException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>The D-Bus error name, or the empty string when it was raised locally.</summary>
    public string ErrorName { get; } = "";
}

/// <summary>
///     The service refused because the application does not hold a permission.
/// </summary>
/// <remarks>
///     <para>
///         This type is doc 01's "no <c>IsPermissionGranted</c> boolean" rule made
///         structural. There is no way to ask; an application posts the critical
///         notification and catches this, or it wraps the control in a
///         <c>PermissionGate</c>. The reason is that a query API teaches applications to
///         branch on authority, which produces two code paths of which one is never
///         tested — and the untested one is the path that runs on the machine where the
///         user said no.
///     </para>
///     <para>
///         ⚠ <b>The permission travels, not just the message.</b> An exception whose only
///         content was prose would force every caller into string matching, and a
///         <c>PermissionGate</c> cannot render "ask for the microphone" from a sentence.
///         <see cref="Permission" /> is the <see cref="Trinix.Bundle.Permissions" /> flag
///         from the one vocabulary <c>Info.json</c> is signed against, so the refusal and
///         the manifest cannot disagree about what was wanted.
///     </para>
/// </remarks>
public sealed class PermissionDeniedException : TrinixServiceException {
    /// <summary>Refuse a call, naming the permission it needed.</summary>
    /// <param name="permission">Exactly one flag; not a combination.</param>
    /// <param name="explanation">What the caller was trying to do.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permission" /> is not a single permission.</exception>
    public PermissionDeniedException(Permissions permission, string explanation)
        : base(ServiceErrors.PermissionDenied, $"{BundlePermissions.NameOf(permission)}: {explanation}") {
        Permission = permission;
        PermissionName = BundlePermissions.NameOf(permission);
        Explanation = explanation;
    }

    PermissionDeniedException(string permissionName, Permissions permission, string explanation)
        : base(ServiceErrors.PermissionDenied, $"{permissionName}: {explanation}") {
        Permission = permission;
        PermissionName = permissionName;
        Explanation = explanation;
    }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    public PermissionDeniedException() { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    /// <param name="message">What to tell the developer.</param>
    public PermissionDeniedException(string message) : base(message) { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    /// <param name="message">What to tell the developer.</param>
    /// <param name="innerException">What caused it.</param>
    public PermissionDeniedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    ///     The permission that was missing, or <see cref="Permissions.None" /> when the
    ///     service named one this build has never heard of.
    /// </summary>
    public Permissions Permission { get; }

    /// <summary>
    ///     The permission as it is spelled in <c>Info.json</c>, even when
    ///     <see cref="Permission" /> is <see cref="Permissions.None" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both are carried because they can disagree, and the disagreement is
    ///     information. A newer shell refusing on a permission this application's SDK
    ///     predates should still be able to say the word out loud — "this needs
    ///     <c>devices.neural</c>" is a message a person can act on, whereas
    ///     <see cref="Permissions.None" /> on its own reads as a bug.
    /// </remarks>
    public string PermissionName { get; } = "";

    /// <summary>What the caller was trying to do, without the permission prefix.</summary>
    public string Explanation { get; } = "";

    /// <summary>
    ///     The message body to put in the D-Bus error reply.
    /// </summary>
    /// <remarks>
    ///     D-Bus errors are a name plus one string, and the name has to stay
    ///     <c>io.trinix.Error.PermissionDenied</c> so that a generic client can classify
    ///     the failure without knowing the vocabulary. That leaves the string to carry
    ///     the permission, and the encoding is <c>"&lt;permission&gt;: &lt;explanation&gt;"</c>.
    ///     <para>
    ///         ⚠ It is unambiguous only because a Trinix permission name contains no
    ///         colon and no space — <c>files.home</c>, <c>system.automation</c>. That is a
    ///         property of <see cref="BundlePermissions" />, enforced there and depended
    ///         on here, and it is why the split is on the first <c>": "</c> rather than on
    ///         anything cleverer.
    ///     </para>
    /// </remarks>
    public string ToErrorMessage() => $"{PermissionName}: {Explanation}";

    /// <summary>
    ///     Rebuild the exception on the client from what came back over the bus.
    /// </summary>
    /// <param name="errorMessage">The body of the D-Bus error reply.</param>
    /// <remarks>
    ///     A message that does not parse is not an error worth adding an error about: the
    ///     whole string becomes <see cref="Explanation" />, <see cref="Permission" /> is
    ///     <see cref="Permissions.None" />, and the caller still learns that it was
    ///     refused, which is the part it must handle.
    /// </remarks>
    public static PermissionDeniedException FromErrorMessage(string errorMessage) {
        ArgumentNullException.ThrowIfNull(errorMessage);

        var separator = errorMessage.IndexOf(": ", StringComparison.Ordinal);
        if (separator <= 0) {
            return new PermissionDeniedException("", Permissions.None, errorMessage);
        }

        var name = errorMessage[..separator];
        var explanation = errorMessage[(separator + 2)..];
        return BundlePermissions.TryParse(name, out var permission)
            ? new PermissionDeniedException(name, permission, explanation)
            : new PermissionDeniedException(name, Permissions.None, explanation);
    }
}

/// <summary>
///     The service implements an older version of the contract than the caller was built
///     against.
/// </summary>
/// <remarks>
///     Doc 02 § Failure, restart and versioning: the proxy refuses rather than calling a
///     member that may not be there, "with a message naming the system version required".
///     ⚠ The version is the <i>contract's</i>, not the system's; mapping one to the other
///     is <c>minimumSystemVersion</c> in <c>Info.json</c> and belongs to the launcher, not
///     here.
/// </remarks>
public sealed class ServiceVersionException : TrinixServiceException {
    /// <summary>Refuse a call because the service is too old.</summary>
    /// <param name="interfaceName">The D-Bus interface that is behind.</param>
    /// <param name="required">The version the caller was built against.</param>
    /// <param name="available">The version the service reports.</param>
    public ServiceVersionException(string interfaceName, int required, int available)
        : base(
            ServiceErrors.VersionTooOld,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} on this system is version {1}; this application needs version {2}.",
                interfaceName,
                available,
                required
            )
        ) {
        InterfaceName = interfaceName;
        Required = required;
        Available = available;
    }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    public ServiceVersionException() { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    /// <param name="message">What to tell the developer.</param>
    public ServiceVersionException(string message) : base(message) { }

    /// <summary>Required by the exception guidelines; never thrown by Trinix itself.</summary>
    /// <param name="message">What to tell the developer.</param>
    /// <param name="innerException">What caused it.</param>
    public ServiceVersionException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>The D-Bus interface that is behind.</summary>
    public string InterfaceName { get; } = "";

    /// <summary>The contract version the caller was built against.</summary>
    public int Required { get; }

    /// <summary>The contract version the service reports.</summary>
    public int Available { get; }
}

/// <summary>
///     The D-Bus error names Trinix's own services raise, and the translation between them
///     and exceptions.
/// </summary>
/// <remarks>
///     <para>
///         Both directions live here rather than in the generator's output, which is the
///         same argument doc 01 makes for generating the proxies at all: a client that
///         maps error names and a server that produces them are two halves of one
///         decision, and two hand-written halves will eventually disagree. The generated
///         code calls <see cref="ToErrorReply" /> on the way out and
///         <see cref="TranslateAsync{T}" /> on the way back in, and neither half restates
///         the table.
///     </para>
///     <para>
///         ⚠ These strings are a wire format and are read by software that is not Trinix
///         — <c>gdbus</c>, a Flatpak's error handler, a log. Renaming one is a
///         compatibility break with no compiler on the far side.
///     </para>
/// </remarks>
public static class ServiceErrors {
    /// <summary>The caller lacks a permission. See <see cref="PermissionDeniedException" />.</summary>
    public const string PermissionDenied = "io.trinix.Error.PermissionDenied";

    /// <summary>The service is older than the caller's contract.</summary>
    public const string VersionTooOld = "io.trinix.Error.VersionTooOld";

    /// <summary>The arguments were well-formed on the wire and wrong.</summary>
    public const string InvalidArgument = "io.trinix.Error.InvalidArgument";

    /// <summary>
    ///     The thing the call named is gone — a withdrawn notification, a clipboard offer
    ///     that has been replaced.
    /// </summary>
    /// <remarks>
    ///     ⚠ Distinct from <see cref="InvalidArgument" /> on purpose. Doc 02 makes update
    ///     and withdraw first-class precisely so that an application can drive one
    ///     notification through a long-running operation, and such an application will
    ///     routinely race the user dismissing it. "You referred to something that no
    ///     longer exists" is an expected outcome of a correct program; "your argument is
    ///     wrong" is a bug. Collapsing them would make the log useless.
    /// </remarks>
    public const string NoSuchObject = "io.trinix.Error.NoSuchObject";

    /// <summary>The service understood and does not implement this here.</summary>
    public const string Unsupported = "io.trinix.Error.Unsupported";

    /// <summary>
    ///     Turn an exception raised inside a service implementation into the
    ///     <c>(name, message)</c> pair a D-Bus error reply carries.
    /// </summary>
    /// <param name="exception">What the implementation threw.</param>
    /// <returns>The error name and the message to put on the wire.</returns>
    /// <remarks>
    ///     ⚠ Anything that is not a <see cref="TrinixServiceException" /> becomes
    ///     <c>org.freedesktop.DBus.Error.Failed</c> with <see cref="Exception.Message" />
    ///     and <b>nothing else</b> — no type name, no stack. A service that leaks the
    ///     shape of its own internals into a reply is telling an untrusted caller which
    ///     code path it reached, and doc 04's threat model has the caller as the
    ///     untrusted party. The full exception belongs in the journal, where the person
    ///     debugging it can see it and the application cannot.
    /// </remarks>
    public static (string Name, string Message) ToErrorReply(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch {
            PermissionDeniedException denied => (PermissionDenied, denied.ToErrorMessage()),
            TrinixServiceException service when service.ErrorName.Length > 0 =>
                (service.ErrorName, service.Message),
            ArgumentException => (InvalidArgument, exception.Message),
            NotSupportedException => (Unsupported, exception.Message),
            _ => ("org.freedesktop.DBus.Error.Failed", exception.Message)
        };
    }

    /// <summary>
    ///     Turn a D-Bus error reply back into the exception the caller should catch.
    /// </summary>
    /// <param name="reply">What the transport raised.</param>
    /// <remarks>
    ///     An error name Trinix does not define is left alone: it is returned as a
    ///     <see cref="TrinixServiceException" /> carrying the name verbatim, because the
    ///     compat faces and the standard interfaces raise names this table has no reason
    ///     to enumerate, and inventing a Trinix meaning for
    ///     <c>org.freedesktop.DBus.Error.ServiceUnknown</c> would lose the one word that
    ///     tells a developer their daemon is not running.
    /// </remarks>
    public static Exception FromErrorReply(DBusErrorReplyException reply) {
        ArgumentNullException.ThrowIfNull(reply);

        return reply.ErrorName switch {
            PermissionDenied => PermissionDeniedException.FromErrorMessage(reply.ErrorMessage),
            _ => new TrinixServiceException(reply.ErrorName, reply.ErrorMessage)
        };
    }

    /// <summary>
    ///     Await a generated proxy call, applying the caller's cancellation and mapping
    ///     the failure.
    /// </summary>
    /// <typeparam name="T">The reply value.</typeparam>
    /// <param name="call">The transport's task, already sent.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The message is already on the wire by the time this is called</b>, and
    ///         that is not an accident. <c>MessageWriter</c> is a <c>ref struct</c>, so the
    ///         generated builder that fills it cannot be part of an <c>async</c> method;
    ///         the proxy therefore builds and sends synchronously and hands the resulting
    ///         task here. The consequence is honest and worth knowing: cancelling stops
    ///         the caller waiting, it does not un-send the call. D-Bus has no cancel, and a
    ///         proxy that pretended otherwise would let an application believe a
    ///         notification it cancelled was never posted.
    ///     </para>
    /// </remarks>
    public static async Task<T> TranslateAsync<T>(Task<T> call, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(call);

        try {
            return await call.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (DBusErrorReplyException reply) {
            throw FromErrorReply(reply);
        }
    }

    /// <summary>
    ///     Await a generated proxy call that has no reply value.
    /// </summary>
    /// <param name="call">The transport's task, already sent.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static async Task TranslateAsync(Task call, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(call);

        try {
            await call.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (DBusErrorReplyException reply) {
            throw FromErrorReply(reply);
        }
    }
}

namespace Trinix.Services.Contracts;

/// <summary>
///     How loud a notification is allowed to be.
/// </summary>
/// <remarks>
///     Three levels rather than the freedesktop hint bag, because doc 02 § Notifications
///     makes urgency the thing Focus filters on, and a filter needs a total order.
///     ⚠ The compat face's <c>urgency</c> hint maps onto this and loses nothing; the
///     reverse mapping loses <see cref="Critical" />'s meaning, which is why the compat
///     face can never raise one.
///     <para>
///         ⚠ Declared over <c>uint</c>, not over the default <c>int</c>, because the
///         underlying type <i>is</i> the wire type: the generator marshals an enum as
///         whatever it is declared over, so a plain <c>enum</c> here would put an <c>i</c>
///         in the introspection XML where every other desktop protocol carrying an urgency
///         puts a <c>u</c>. Nothing would break; it would just be wrong in a document
///         other people read.
///     </para>
/// </remarks>
public enum NotificationUrgency : uint {
    /// <summary>Shown in the notification centre; never interrupts.</summary>
    Low = 0,

    /// <summary>The default: a banner, subject to Focus.</summary>
    Normal = 1,

    /// <summary>
    ///     Pierces Focus and Do Not Disturb.
    /// </summary>
    /// <remarks>
    ///     ⚠ Requires <see cref="Trinix.Bundle.Permissions.SystemNotificationsCritical" />,
    ///     which doc 04 says the Store does not grant automatically. Posting one without
    ///     it throws <see cref="PermissionDeniedException" /> — there is deliberately no
    ///     way to ask first, per doc 01: an application posts what it means and handles
    ///     the refusal by falling back to <see cref="Normal" />.
    /// </remarks>
    Critical = 2
}

/// <summary>
///     One of the named things a person can do from a notification.
/// </summary>
/// <remarks>
///     Doc 02: actions "activate the application rather than running a callback in the
///     shell", so that a notification that survives its app's exit still works. The
///     consequence for this record is that <see cref="Key" /> must be meaningful to the
///     application <i>after a restart</i> — it is passed back on activation and is the
///     only thing the new process has to go on. A key that encodes a delegate identity,
///     an index into a list, or anything else that lives in the posting process is a bug
///     that only shows up after the app is killed.
/// </remarks>
/// <param name="Key">The application's own identifier for the action.</param>
/// <param name="Title">What the button says. ⚠ Not localised by the shell — the app sends the user's language.</param>
[ServiceRecord]
public sealed record NotificationAction(string Key, string Title);

/// <summary>
///     The inline reply field on a notification.
/// </summary>
/// <remarks>
///     First-class rather than an extension, because doc 02 is explicit that Messages and
///     Mail are unusable without it and that retrofitting it means a second protocol. It
///     is optional per notification — <see cref="NotificationRequest.Reply" /> is
///     <see langword="null" /> for the notifications that have no reply box — and the
///     generator marshals that <see langword="null" /> as a zero-length array of this
///     structure, which is D-Bus's only way to spell "maybe".
/// </remarks>
/// <param name="Placeholder">The greyed-out prompt in the empty field.</param>
/// <param name="SubmitTitle">What the send button says.</param>
[ServiceRecord]
public sealed record NotificationReply(string Placeholder, string SubmitTitle);

/// <summary>
///     Which notification, exactly.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § Notifications: "a notification is a record with an identity, not a
///         string". The identity is a pair, not a number, and both halves are load-bearing.
///     </para>
///     <para>
///         <see cref="Application" /> is the <b>bundle identity</b> doc 02 § The two-faces
///         rule requires — the thing the sandbox can attribute a notification to.
///         ⚠ <b>The caller does not choose it.</b> The service fills it in from the
///         connection's credentials, so the value an application sends in
///         <see cref="INotifications.UpdateAsync" /> is checked, not trusted: an
///         identifier belonging to somebody else is a refusal, not an update. This is the
///         one place where "the compat face gets the process's identity rather than a
///         signed bundle identity" has teeth — a caller arriving over
///         <c>org.freedesktop.Notifications</c> gets an unattested identity here and the
///         permissions that go with it.
///     </para>
///     <para>
///         <see cref="Sequence" /> is the service's, unique within the application, and it
///         is what makes update and withdraw first-class. Doc 02 names the failure it
///         prevents: a download notification posted once per percent, because the poster
///         had no handle on the one it already had.
///     </para>
/// </remarks>
/// <param name="Application">The posting bundle's identifier, as the service attributed it.</param>
/// <param name="Sequence">The service's number for this notification, unique within that application.</param>
[ServiceRecord]
public readonly record struct NotificationId(string Application, uint Sequence);

/// <summary>
///     What an application asks the shell to show.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02's model is <c>(app, id, title, body, urgency, actions[], reply?, group,
///         timestamp, expiry)</c>, and this record is that tuple minus the three fields an
///         application is not allowed to author. <c>app</c> and <c>id</c> are the
///         service's (see <see cref="NotificationId" />) and <c>timestamp</c> is the
///         service's too — a notification stamped by its poster is a notification whose
///         ordering in the centre can be forged by a clock, and history is sorted by it.
///     </para>
///     <para>
///         The same record is passed to <see cref="INotifications.UpdateAsync" />, which
///         is deliberate: an update is a <i>replacement</i>, not a patch. A patch shape
///         would need every field to be optional and would make "clear the body" and
///         "leave the body alone" the same message.
///     </para>
/// </remarks>
/// <param name="Title">The bold line. Required; an empty title is an <see cref="ServiceErrors.InvalidArgument" />.</param>
/// <param name="Body">The rest. May be empty.</param>
/// <param name="Urgency">See <see cref="NotificationUrgency" />.</param>
/// <param name="Actions">
///     Named buttons, in the order they are shown. ⚠ The shell may show fewer than were
///     asked for — a banner has room for two — and the rest live in the notification
///     centre. An application that needs all of them visible has designed a dialog.
/// </param>
/// <param name="Reply">The inline reply field, or <see langword="null" /> for none.</param>
/// <param name="Group">
///     The application's own grouping key. Doc 02 groups by app and then by this,
///     collapsing after three. Empty means "group with the app's ungrouped ones".
/// </param>
/// <param name="Expiry">
///     How long before the shell retires it on its own.
///     ⚠ <see cref="TimeSpan.Zero" /> means <b>never</b>, not "immediately". D-Bus has no
///     null for a scalar, so one value has to carry "no opinion", and zero is the one that
///     cannot be confused with a real duration a caller meant. A notification that should
///     vanish at once is not a notification.
/// </param>
[ServiceRecord]
public sealed record NotificationRequest(
    string Title,
    string Body,
    NotificationUrgency Urgency,
    IReadOnlyList<NotificationAction> Actions,
    NotificationReply? Reply,
    string Group,
    TimeSpan Expiry
);

/// <summary>
///     A person pressed one of a notification's actions.
/// </summary>
/// <remarks>
///     ⚠ This signal is the <b>running-application</b> half of doc 02's rule that actions
///     activate the application. When the app is not running the shell launches it and the
///     same <c>(id, action)</c> pair arrives as a <c>Launch</c> instead (doc 01
///     § The application). Two transports, one payload, deliberately: an application that
///     handles the record handles both, and an application that only subscribes to the
///     signal has a bug that appears the first time it is not already open.
/// </remarks>
/// <param name="Id">Which notification.</param>
/// <param name="ActionKey">
///     The <see cref="NotificationAction.Key" /> that was pressed, or the empty string when
///     the notification itself was clicked rather than one of its buttons.
/// </param>
[ServiceRecord]
public sealed record NotificationActivation(NotificationId Id, string ActionKey);

/// <summary>
///     A person typed into a notification's inline reply and sent it.
/// </summary>
/// <param name="Id">Which notification.</param>
/// <param name="Text">What they wrote. ⚠ Arbitrary user input from outside the application's own window; treat it as such.</param>
[ServiceRecord]
public sealed record NotificationSubmission(NotificationId Id, string Text);

/// <summary>
///     Post, update and withdraw notifications, and hear about what a person did with them.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § Notifications, native face. The compat face —
///         <c>org.freedesktop.Notifications</c>, which every GTK, Qt and Electron
///         application calls — is a hand-written projection onto the same implementation
///         and is not this interface: its <c>Notify</c> returns a caller-chosen id, has no
///         grouping, no reply, and hints rather than urgency.
///     </para>
///     <para>
///         There is no <c>List</c>, no <c>GetHistory</c> and no way to read another
///         application's notifications, and the absence is the enforcement. Doc 02 puts
///         the 7-day history in the shell's own store, searchable by Beacon; an interface
///         that could enumerate it would make every notification an application ever
///         posted readable by every other application that asked, which no permission in
///         doc 04's vocabulary corresponds to.
///     </para>
///     <para>
///         ⚠ <b>Posting needs no permission.</b> Doc 02 § Notifications is explicit that
///         per-app notification permission is opt-out: an application may post from first
///         launch and the first notification carries a "keep showing these?"
///         affordance. Opt-in prompts on first launch train people to say yes to
///         everything, which is the failure mode doc 04 exists to avoid. The single
///         permissioned thing here is <see cref="NotificationUrgency.Critical" />.
///     </para>
/// </remarks>
[TrinixService(
    "io.trinix.Notifications",
    BusName = "io.trinix.Shell",
    ObjectPath = "/io/trinix/Notifications",
    Version = 1
)]
public interface INotifications {
    /// <summary>Show a notification.</summary>
    /// <param name="request">What to show.</param>
    /// <param name="cancellationToken">Stops the caller waiting for the id; see the remarks.</param>
    /// <returns>The identity to use for <see cref="UpdateAsync" /> and <see cref="WithdrawAsync" />.</returns>
    /// <exception cref="PermissionDeniedException">
    ///     <see cref="NotificationRequest.Urgency" /> is
    ///     <see cref="NotificationUrgency.Critical" /> and the application does not hold
    ///     <see cref="Trinix.Bundle.Permissions.SystemNotificationsCritical" />.
    /// </exception>
    /// <remarks>
    ///     ⚠ Cancelling does not un-post. The call is on the wire before the returned task
    ///     exists — see <see cref="ServiceErrors.TranslateAsync{T}" /> for why — so a
    ///     cancelled post whose notification appeared anyway is expected behaviour, and
    ///     the remedy is <see cref="WithdrawAsync" />, not a retry.
    /// </remarks>
    Task<NotificationId> PostAsync(NotificationRequest request, CancellationToken cancellationToken);

    /// <summary>Replace what a notification says, in place.</summary>
    /// <param name="id">Which notification. Must belong to the calling application.</param>
    /// <param name="request">The new content, in full.</param>
    /// <param name="cancellationToken">Stops the caller waiting.</param>
    /// <exception cref="TrinixServiceException">
    ///     <see cref="ServiceErrors.NoSuchObject" /> when the notification has already been
    ///     dismissed or expired — which is a race an application will lose routinely and
    ///     should treat as "nothing to update" rather than as a failure.
    /// </exception>
    Task UpdateAsync(NotificationId id, NotificationRequest request, CancellationToken cancellationToken);

    /// <summary>Take a notification back.</summary>
    /// <param name="id">Which notification. Must belong to the calling application.</param>
    /// <param name="cancellationToken">Stops the caller waiting.</param>
    /// <remarks>
    ///     Withdrawing something already gone succeeds. ⚠ Unlike
    ///     <see cref="UpdateAsync" />, because the caller's intent — "this should not be on
    ///     screen" — is already satisfied, and an application that had to catch an
    ///     exception to learn it got what it wanted would wrap every call in a swallow.
    /// </remarks>
    Task WithdrawAsync(NotificationId id, CancellationToken cancellationToken);

    /// <summary>Hear when one of this application's notification actions is pressed.</summary>
    /// <param name="handler">Called on the connection's reader; do not block it.</param>
    /// <param name="cancellationToken">Stops the caller waiting for the bus to confirm the subscription.</param>
    /// <returns>Dispose to unsubscribe.</returns>
    [ServiceSignal("Activated")]
    Task<IDisposable> WatchActivatedAsync(
        Action<NotificationActivation> handler,
        CancellationToken cancellationToken
    );

    /// <summary>Hear when a person sends an inline reply.</summary>
    /// <param name="handler">Called on the connection's reader; do not block it.</param>
    /// <param name="cancellationToken">Stops the caller waiting for the bus to confirm the subscription.</param>
    /// <returns>Dispose to unsubscribe.</returns>
    [ServiceSignal("Replied")]
    Task<IDisposable> WatchRepliedAsync(
        Action<NotificationSubmission> handler,
        CancellationToken cancellationToken
    );
}

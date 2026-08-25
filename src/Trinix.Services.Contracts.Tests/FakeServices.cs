using Tmds.DBus.Protocol;
using Trinix.Bundle;

namespace Trinix.Services.Contracts.Tests;

/// <summary>
///     A notifications service that records what it was told and says what it was set to
///     say.
/// </summary>
/// <remarks>
///     Derived from the <i>generated</i> <c>NotificationsHandler</c>, which is the shape doc
///     02's daemons will take: the abstract members are the contract, the dispatcher and the
///     signal emitters come from the interface, and the implementation writes neither. ⚠ It
///     also demonstrates the rule the type system cannot state — the service refuses
///     <see cref="NotificationUrgency.Critical" /> by throwing, never by returning a status,
///     because doc 01's surface has no way to ask first.
/// </remarks>
sealed class FakeNotifications : NotificationsHandler {
    public FakeNotifications(DBusConnection connection) : base(connection) { }

    /// <summary>What the last call carried, decoded by the generated reader.</summary>
    public NotificationRequest? LastRequest { get; private set; }

    /// <summary>The id the last update or withdraw named.</summary>
    public NotificationId LastId { get; private set; }

    /// <summary>Whether the application is allowed to raise a critical notification.</summary>
    public bool AllowCritical { get; set; }

    /// <summary>Set to make <see cref="UpdateAsync" /> report a notification that is gone.</summary>
    public bool NotificationIsGone { get; set; }

    /// <summary>Raise the activation signal at one peer, as a real shell would.</summary>
    public void Activate(string destination, NotificationActivation activation) =>
        EmitActivated(destination, activation);

    protected override Task<NotificationId> PostAsync(
        NotificationRequest request,
        CancellationToken cancellationToken
    ) {
        if (request.Urgency == NotificationUrgency.Critical && !AllowCritical) {
            throw new PermissionDeniedException(
                Permissions.SystemNotificationsCritical,
                "posting a notification that pierces Focus"
            );
        }

        LastRequest = request;
        return Task.FromResult(new NotificationId("io.example.notes", 7));
    }

    protected override Task UpdateAsync(
        NotificationId id,
        NotificationRequest request,
        CancellationToken cancellationToken
    ) {
        if (NotificationIsGone) {
            throw new TrinixServiceException(
                ServiceErrors.NoSuchObject,
                "that notification has already been dismissed"
            );
        }

        LastId = id;
        LastRequest = request;
        return Task.CompletedTask;
    }

    protected override Task WithdrawAsync(NotificationId id, CancellationToken cancellationToken) {
        LastId = id;
        return Task.CompletedTask;
    }
}

/// <summary>A clipboard that reports one fixed offer.</summary>
/// <remarks>
///     ⚠ <see cref="ReadAsync" /> and <see cref="OfferAsync" /> throw rather than pretend.
///     Both carry a file descriptor, and <see cref="LoopbackBus" /> has no socket to send one
///     over; a fake that returned a handle would be testing that the harness can lie.
/// </remarks>
sealed class FakeClipboard : ClipboardHandler {
    public FakeClipboard(DBusConnection connection) : base(connection) { }

    /// <summary>What <see cref="GetOfferAsync" /> answers, and what <see cref="Change" /> broadcasts.</summary>
    public ClipboardOffer Offer { get; set; } =
        new(["text/plain;charset=utf-8", "text/html"], Sensitive: false, Serial: 42);

    /// <summary>Broadcast the current offer.</summary>
    public void Change() => EmitChanged(Offer);

    protected override Task<ClipboardOffer> GetOfferAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Offer);

    protected override Task<Microsoft.Win32.SafeHandles.SafeFileHandle> ReadAsync(
        string mimeType,
        ulong serial,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException("this harness has no socket, so it cannot pass a descriptor");

    protected override Task<ulong> OfferAsync(
        IReadOnlyList<ClipboardRepresentation> representations,
        bool sensitive,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException("this harness has no socket, so it cannot receive a descriptor");
}

/// <summary>A system service with a fixed identity and a changeable appearance.</summary>
sealed class FakeSystem : SystemHandler {
    public FakeSystem(DBusConnection connection) : base(connection) { }

    /// <summary>What <see cref="GetAppearanceAsync" /> answers.</summary>
    public Appearance Appearance { get; set; } =
        new(ColorScheme.Dark, "#2f6fdb", ReduceMotion: true, IncreaseContrast: false);

    /// <summary>Broadcast the current appearance.</summary>
    public void Change() => EmitAppearanceChanged(Appearance);

    protected override Task<SystemRelease> GetReleaseAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SystemRelease("0.3", "20260825.1", "Trinix 0.3"));

    protected override Task<Appearance> GetAppearanceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Appearance);
}

using Microsoft.Win32.SafeHandles;

namespace Trinix.Services.Contracts;

/// <summary>
///     One representation of what is on the clipboard: a MIME type and a file descriptor
///     to read the bytes from.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An fd, not a byte array</b>, and doc 02 § The transport decision says why
///         in one line: "Bulk data — a file being previewed, a screen recording — is a file
///         descriptor passed over D-Bus, never a byte array in a message." A clipboard is
///         the case that proves it — doc 02 § Clipboard names a 400 MB image from an
///         editor as a real input, and a 400 MB D-Bus message is a dead bus.
///     </para>
///     <para>
///         The fd is the read end of a pipe or a sealed memfd; the caller owns it and must
///         dispose it. It is <see cref="SafeFileHandle" /> rather than an <c>int</c> so
///         that the ownership is the type's problem: an <c>int</c> that is forgotten is an
///         fd leak in a long-lived shell, and an <c>int</c> that is closed twice is a
///         use-after-free that lands on whatever unrelated file inherited the number.
///     </para>
/// </remarks>
/// <param name="MimeType">e.g. <c>text/plain;charset=utf-8</c>, <c>image/png</c>.</param>
/// <param name="Data">The read end. The receiver disposes it.</param>
[ServiceRecord]
public sealed record ClipboardRepresentation(string MimeType, SafeFileHandle Data);

/// <summary>
///     What is on the clipboard right now, described without transferring it.
/// </summary>
/// <remarks>
///     <para>
///         Wayland's clipboard is offer-based — the source advertises MIME types and the
///         data moves only when a target asks — and this record is that offer, kept
///         separate from the data for the same reason Wayland keeps it separate: a paste
///         menu needs to know a PNG is available without materialising the PNG.
///     </para>
///     <para>
///         <see cref="Serial" /> is what makes a read race-free. Between
///         <see cref="IClipboard.GetOfferAsync" /> and
///         <see cref="IClipboard.ReadAsync" /> the user can copy something else; without a
///         serial the second call silently returns the new thing under the old offer's
///         type. ⚠ It is a <c>ulong</c> and it is monotonic within a session only — doc 02
///         puts the clipboard in <c>trinix-shell</c> and says the clipboard dies with the
///         shell, so a serial that survived a restart would be claiming a continuity that
///         does not exist.
///     </para>
/// </remarks>
/// <param name="MimeTypes">Every type the current offer can produce, best first.</param>
/// <param name="Sensitive">
///     The offer was marked as a password or equivalent. Doc 02: never recorded in
///     history, and cleared from the current clipboard after 45 s. ⚠ An application is
///     told this so it can decline to log or preview the value; it is <b>not</b> a
///     permission and reading is not blocked.
/// </param>
/// <param name="Serial">Which offer this is. Pass it back to <see cref="IClipboard.ReadAsync" />.</param>
[ServiceRecord]
public sealed record ClipboardOffer(IReadOnlyList<string> MimeTypes, bool Sensitive, ulong Serial);

/// <summary>
///     Read and write the clipboard.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § Clipboard. The service lives in <c>trinix-shell</c> because the
///         clipboard's state <i>is</i> shell state, and the interesting behaviour it owns
///         is materialisation: when an application offers something, the shell copies it
///         at the moment of the offer and re-offers it itself, so that the paste still
///         works after the source has quit. ⚠ Above the 8 MB limit it does not, and the
///         paste-after-quit genuinely fails — which is the honest behaviour, and it is why
///         <see cref="OfferAsync" /> cannot promise durability in its signature.
///     </para>
///     <para>
///         <b>There is no history on this interface, and that is the design.</b> Doc 02:
///         "An application may read the <i>current</i> clipboard when focused and may never
///         read the history — the history is the shell's, and pasting from it is the user's
///         action." History is 20 entries, in memory, dropped on lock; a method here that
///         returned them would hand every application a keystroke-adjacent record of
///         everything the user copied, and no permission in doc 04's vocabulary grants
///         that because it is not a capability Trinix offers.
///     </para>
///     <para>
///         ⚠ Reading is gated on <i>focus</i>, not on a declared permission, which is why
///         nothing here throws <see cref="PermissionDeniedException" /> for the ordinary
///         path. Focus is a compositor fact about a surface, and doc 02 § The transport
///         decision draws the line there: if the answer to "who is asking?" is a window, it
///         is Wayland's to know. The service asks the compositor; the application declares
///         nothing.
///     </para>
/// </remarks>
[TrinixService(
    "io.trinix.Clipboard",
    BusName = "io.trinix.Shell",
    ObjectPath = "/io/trinix/Clipboard",
    Version = 1
)]
public interface IClipboard {
    /// <summary>What is on the clipboard, without moving any of it.</summary>
    /// <param name="cancellationToken">Stops the caller waiting.</param>
    /// <returns>The current offer. An empty clipboard is an offer with no MIME types, not an error.</returns>
    Task<ClipboardOffer> GetOfferAsync(CancellationToken cancellationToken);

    /// <summary>Ask for one representation of the current offer.</summary>
    /// <param name="mimeType">One of <see cref="ClipboardOffer.MimeTypes" />.</param>
    /// <param name="serial">The <see cref="ClipboardOffer.Serial" /> the type came from.</param>
    /// <param name="cancellationToken">Stops the caller waiting for the descriptor.</param>
    /// <returns>The read end of a pipe. ⚠ The caller owns it and must dispose it.</returns>
    /// <exception cref="TrinixServiceException">
    ///     <see cref="ServiceErrors.NoSuchObject" /> when <paramref name="serial" /> is no
    ///     longer the current offer — the user copied something else in between — or when
    ///     the offer cannot produce <paramref name="mimeType" />.
    /// </exception>
    /// <remarks>
    ///     ⚠ Returning the descriptor does not mean the bytes are there. It is a pipe: the
    ///     writer may still be running, and for a large offer it certainly is. A caller
    ///     that needs the whole value must read to EOF, and a caller that gives up must
    ///     close the fd so the writer stops.
    /// </remarks>
    Task<SafeFileHandle> ReadAsync(string mimeType, ulong serial, CancellationToken cancellationToken);

    /// <summary>Put something on the clipboard.</summary>
    /// <param name="representations">
    ///     Every form of the same value, best first. Offering one value in several types is
    ///     the whole point of the model — an editor offers <c>text/html</c> and
    ///     <c>text/plain</c> and the paste target picks.
    /// </param>
    /// <param name="sensitive">
    ///     Mark it as a password or equivalent. Doc 02: kept out of history entirely and
    ///     cleared after 45 s.
    /// </param>
    /// <param name="cancellationToken">Stops the caller waiting.</param>
    /// <returns>The serial the new offer was given.</returns>
    /// <remarks>
    ///     ⚠ The descriptors are <b>read</b> by the shell, immediately, up to the 8 MB
    ///     materialisation limit. An application that offers a pipe it has not filled will
    ///     block the shell's materialisation until it does — which is doc 02 § Failure's
    ///     rule about a service never calling into an application while holding a lock,
    ///     seen from the other side. Offer a memfd or a filled pipe, not a promise.
    /// </remarks>
    Task<ulong> OfferAsync(
        IReadOnlyList<ClipboardRepresentation> representations,
        bool sensitive,
        CancellationToken cancellationToken
    );

    /// <summary>Hear when the clipboard's contents change.</summary>
    /// <param name="handler">Called on the connection's reader; do not block it.</param>
    /// <param name="cancellationToken">Stops the caller waiting for the bus to confirm the subscription.</param>
    /// <returns>Dispose to unsubscribe.</returns>
    /// <remarks>
    ///     Carries the offer, not the data, so that following the clipboard costs nothing
    ///     until somebody wants a value. ⚠ A paste target that materialises on every
    ///     change has reimplemented the bug Wayland's offer model exists to avoid.
    /// </remarks>
    [ServiceSignal("Changed")]
    Task<IDisposable> WatchChangedAsync(Action<ClipboardOffer> handler, CancellationToken cancellationToken);
}

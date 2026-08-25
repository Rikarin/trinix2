namespace Trinix.Services.Contracts;

/// <summary>Light or dark.</summary>
/// <remarks>
///     Two values and no "auto". ⚠ Auto is a <i>setting</i>, and the setting resolves to
///     one of these before it reaches an application: an application that received "auto"
///     would have to know the schedule, the location and the ambient sensor to render a
///     colour, which is three subsystems reimplemented in every application. Doc 01
///     § The design language makes light and dark "the same stylesheet with different
///     tokens", and a token cannot be a maybe.
///     <para>
///         ⚠ Over <c>uint</c> because an enum's underlying type is its wire type; see
///         <see cref="NotificationUrgency" />.
///     </para>
/// </remarks>
public enum ColorScheme : uint {
    /// <summary>The default.</summary>
    Light = 0,

    /// <summary>Dark.</summary>
    Dark = 1
}

/// <summary>
///     The system's identity, as the running image reports it.
/// </summary>
/// <remarks>
///     ⚠ This is the <i>system's</i> version, which is a different question from the
///     service contract's version on <see cref="TrinixServiceAttribute.Version" />. An
///     application compares against this for <c>minimumSystemVersion</c> in its
///     <c>Info.json</c>; the proxy compares against the other for whether a member exists.
///     Doc 02 says a coarse system version is the right granularity "while everything
///     ships from one tree" and stops being right once third parties depend on individual
///     protocols — which is what the per-interface version is for.
/// </remarks>
/// <param name="VersionId">
///     <c>VERSION_ID</c> from <c>/usr/lib/os-release</c> — the string
///     <see cref="Trinix.Bundle.SystemVersion.Satisfies" /> compares.
/// </param>
/// <param name="BuildId">The image build this machine is running, for a bug report.</param>
/// <param name="PrettyName">What to put in an About window.</param>
[ServiceRecord]
public sealed record SystemRelease(string VersionId, string BuildId, string PrettyName);

/// <summary>
///     How the user has asked the system to look, in the form an application must obey.
/// </summary>
/// <remarks>
///     <para>
///         Every field here is a <i>runtime knob a person turns in Settings</i>, which is
///         doc 01 § The design language's whole argument for one <c>tokens.yaml</c>: a
///         colour written literally in a stylesheet will not follow the accent, and the bug
///         appears months later in one control. The service is where the knob's current
///         position comes from.
///     </para>
///     <para>
///         ⚠ <see cref="ReduceMotion" /> is here to be turned into a <i>token</i>, not into
///         a branch. Doc 01: "If instead every animation site checks the setting, some will
///         not, and those are the ones that make a user sick." An application that reads
///         this field and writes <c>if (reduceMotion)</c> around one animation has already
///         chosen the failure mode.
///     </para>
/// </remarks>
/// <param name="Scheme">Light or dark, already resolved.</param>
/// <param name="AccentColor">
///     The accent, as <c>#rrggbb</c> in sRGB. ⚠ A string rather than a packed integer
///     because it is the value that appears in <c>.vcss</c> and in a bug report, and
///     because Trinix's own tokens are authored in Oklab — a 32-bit lump would imply a
///     precision and a colour space the token file does not commit to.
/// </param>
/// <param name="ReduceMotion">The accessibility setting. See the remarks.</param>
/// <param name="IncreaseContrast">The accessibility setting.</param>
[ServiceRecord]
public sealed record Appearance(
    ColorScheme Scheme,
    string AccentColor,
    bool ReduceMotion,
    bool IncreaseContrast
);

/// <summary>
///     The small facts about the machine that every application needs and none should
///     discover for itself.
/// </summary>
/// <remarks>
///     <para>
///         Doc 01 § The service surface lists <c>System</c> as "version, appearance,
///         locale, accessibility settings, the appearance-changed event". This contract
///         carries the first two and the event; locale is deliberately absent until doc 18
///         R7 decides what a localised Trinix string <i>is</i>, because a
///         <c>GetLocaleAsync</c> shipped before then would be answered by applications
///         reimplementing formatting, and it would be the answer they kept.
///     </para>
///     <para>
///         ⚠ <b>Everything here is <c>async</c> even though every value is a few bytes the
///         daemon already has in memory.</b> This is the interface where doc 01's rule
///         looks most like ceremony and is most load-bearing: a synchronous
///         <c>System.Appearance</c> that works today because the value is cached becomes a
///         synchronous IPC call the day the appearance is owned by the settings store, and
///         by then a thousand call sites are on the UI thread. The asynchrony is part of
///         the contract, not of the implementation.
///     </para>
///     <para>
///         Nothing here is permissioned. Doc 04's test for a permission is "something an
///         application can do that it could not do by being a process", and reading the
///         version of the OS it is running on is not that.
///     </para>
/// </remarks>
[TrinixService(
    "io.trinix.System",
    BusName = "io.trinix.Daemon",
    ObjectPath = "/io/trinix/System",
    Version = 1
)]
public interface ISystem {
    /// <summary>What system is this.</summary>
    /// <param name="cancellationToken">Stops the caller waiting.</param>
    Task<SystemRelease> GetReleaseAsync(CancellationToken cancellationToken);

    /// <summary>How the user has asked things to look.</summary>
    /// <param name="cancellationToken">Stops the caller waiting.</param>
    Task<Appearance> GetAppearanceAsync(CancellationToken cancellationToken);

    /// <summary>Hear when the appearance changes.</summary>
    /// <param name="handler">Called on the connection's reader; do not block it.</param>
    /// <param name="cancellationToken">Stops the caller waiting for the bus to confirm the subscription.</param>
    /// <returns>Dispose to unsubscribe.</returns>
    /// <remarks>
    ///     ⚠ Carries the whole <see cref="Appearance" />, not the field that changed. A
    ///     delta would let an application apply half a change — the scheme without the
    ///     accent it was chosen against — and the record is four fields.
    /// </remarks>
    [ServiceSignal("AppearanceChanged")]
    Task<IDisposable> WatchAppearanceChangedAsync(
        Action<Appearance> handler,
        CancellationToken cancellationToken
    );
}

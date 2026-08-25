using Trinix.Sandbox;

namespace Trinix.Conformance;

/// <summary>
///     Everything about a conformance run that is not the bundle.
/// </summary>
/// <remarks>
///     ⚠ Every machine-dependent input is a nullable property with a documented
///     discovery fallback, and that is what makes doctor testable. A check whose
///     answer depends on the clock, on <c>/usr/lib/os-release</c> or on whether this
///     machine has a systemd is a check that behaves one way on a developer's Mac and
///     another in CI, and the version-comparison checks in particular are about
///     exactly those inputs. Passing them in means the tests can ask what doctor says
///     about a system running <c>0.4-rc1</c> without one existing.
/// </remarks>
public sealed record DoctorOptions {
    /// <summary>
    ///     Where the trust anchors are, or <see langword="null" /> for the system
    ///     image's <see cref="Trinix.Bundle.TrustStore.SystemDirectory" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ There is normally no trust store on a developer's machine, and doctor
    ///     treats that as a skipped check rather than a failure — see
    ///     <c>SignatureChecks</c>. Refusing to run without one would make the tool
    ///     useless in the place it is most useful.
    /// </remarks>
    public string? TrustDirectory { get; init; }

    /// <summary>
    ///     Run doc 09's submission subset: every <see cref="Finding.StoreGate" />
    ///     warning becomes an error.
    /// </summary>
    public bool Store { get; init; }

    /// <summary>
    ///     What the target system's systemd can be asked for, or <see langword="null" />
    ///     to probe this machine and fall back to
    ///     <see cref="SandboxCapabilities.TrinixToday" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ The fallback is the image's measured capabilities rather than
    ///     <see cref="SandboxCapabilities.Designed" />, because doctor is run on the
    ///     machine that <i>builds</i> the bundle and asked about the machine that will
    ///     <i>run</i> it. Answering from the build host's systemd — which on a Mac is
    ///     no systemd at all — would report a containment nobody will get. Doctor says
    ///     which of the two it used, every time.
    /// </remarks>
    public SandboxCapabilities? Capabilities { get; init; }

    /// <summary>
    ///     The system version to judge <c>minimumSystemVersion</c> against, or
    ///     <see langword="null" /> to read this machine's.
    /// </summary>
    public string? SystemVersion { get; init; }

    /// <summary>The instant to judge certificate validity at. Defaults to now.</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>
    ///     How close a signing certificate may come to expiring before doctor says so.
    /// </summary>
    /// <remarks>
    ///     Thirty days because the failure it prevents is specific: a certificate that
    ///     expires between a build and a release is a bundle that verified on the
    ///     builder's machine and is refused on the user's, with a message about trust
    ///     rather than about time.
    /// </remarks>
    public int CertificateExpiryWarningDays { get; init; } = 30;
}

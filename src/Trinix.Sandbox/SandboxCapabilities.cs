namespace Trinix.Sandbox;

/// <summary>
///     What the systemd on <i>this</i> machine can actually be asked for.
/// </summary>
/// <remarks>
///     <para>
///         This type exists because of a discovery that doc 04 does not account for.
///         <c>base/recipes/systemd/recipe.sh</c> builds systemd 257 with
///         <c>-Dseccomp=disabled</c>, under the heading "security frameworks the base
///         image does not have". Everything in doc 04's "Syscall floor" row —
///         <c>SystemCallFilter=</c>, <c>SystemCallArchitectures=</c>,
///         <c>MemoryDenyWriteExecute=</c> — and two of the five in its "Kernel
///         surfaces" row — <c>RestrictRealtime=</c>, <c>RestrictSUIDSGID=</c> — are
///         implemented by systemd as seccomp filters, and a systemd built without
///         libseccomp does not implement them.
///     </para>
///     <para>
///         ⚠ <b>What it does instead is not verified here.</b> In a unit file on disk
///         an unimplemented directive is an unknown key: systemd logs "Unknown key
///         name … ignoring" and starts the service anyway, which is why
///         <c>trinixd.service</c> can carry <c>MemoryDenyWriteExecute=no</c> and
///         <c>RestrictNamespaces=yes</c> on this image without failing. A transient
///         unit does not take that path — the properties go over D-Bus, and a property
///         the bus handler does not know is an error the method call returns rather
///         than a line in the journal. Which of the two happens decides whether
///         emitting the syscall floor on Trinix as it is built today produces a
///         hardened application or no application at all, and it cannot be settled
///         from a Mac. It has to be settled with a <c>systemd-run</c> in front of you.
///     </para>
///     <para>
///         So the builder takes this as an input rather than deciding. The default is
///         <see cref="Designed" />, which is doc 04's design and assumes a systemd that
///         has everything; <see cref="TrinixToday" /> is what the pinned recipe
///         actually produces. Whichever is passed, the properties that were left out
///         are recorded in <see cref="SandboxUnit.Gaps" /> — an application that is
///         less contained than the design says should be a fact something can log, not
///         a silence.
///     </para>
/// </remarks>
public sealed record SandboxCapabilities {
    /// <summary>
    ///     Doc 04's assumption: a systemd with every property it names.
    /// </summary>
    /// <remarks>
    ///     The default, because the alternative is a library whose out-of-the-box
    ///     behaviour is the degraded one — and a degraded default is the kind of thing
    ///     that survives into a release because nothing ever failed.
    /// </remarks>
    public static SandboxCapabilities Designed { get; } = new();

    /// <summary>
    ///     What <c>base/recipes/systemd/recipe.sh</c> builds as of systemd 257.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deriving this from the recipe by hand is itself a thing that can go
    ///     stale. The durable version is a probe — <c>systemd-analyze</c> reports its
    ///     own feature string, and <c>systemctl show -p …</c> answers whether a
    ///     property exists — and that probe belongs in the wiring slice, on a machine
    ///     that has a systemd to ask.
    /// </remarks>
    public static SandboxCapabilities TrinixToday { get; } = new() { Seccomp = false };

    /// <summary>
    ///     Was systemd built with libseccomp?
    /// </summary>
    /// <remarks>
    ///     Gates <c>SystemCallFilter=</c>, <c>SystemCallArchitectures=</c>,
    ///     <c>MemoryDenyWriteExecute=</c>, <c>RestrictRealtime=</c> and
    ///     <c>RestrictSUIDSGID=</c>. It does <b>not</b> gate <c>NoNewPrivileges=</c>,
    ///     which is a <c>prctl</c> and needs nothing, nor the <c>ProtectKernel*</c>
    ///     and <c>ProtectControlGroups=</c> family, which are mount and capability
    ///     work with a seccomp filter as a bonus rather than as their mechanism.
    /// </remarks>
    public bool Seccomp { get; init; } = true;

    /// <summary>
    ///     May the unit ask for <c>PrivatePIDs=yes</c>?
    /// </summary>
    /// <remarks>
    ///     ⚠ Off by default even though systemd 257 — exactly Trinix's pin — is the
    ///     release that added it. A PID namespace changes what the application's own
    ///     <c>getpid</c> returns and what a debugger attached to it sees, it interacts
    ///     with <c>RootDirectory=</c> through a private <c>/proc</c>, and it has had
    ///     one release of exposure anywhere. <c>ProtectProc=invisible</c> has been in
    ///     since v247 and delivers most of doc 04's "no other processes" row without
    ///     any of that. Turning this on is a decision to make with a VM booted, and
    ///     the flag exists so that it is one flag rather than an edit.
    /// </remarks>
    public bool PrivatePids { get; init; }
}

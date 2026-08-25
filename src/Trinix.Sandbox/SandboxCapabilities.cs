namespace Trinix.Sandbox;

/// <summary>
///     Whether a property this unit sets actually does anything on the system it was
///     built for.
/// </summary>
/// <remarks>
///     <para>
///         Three states rather than two, and the third is the reason the type exists.
///         Doc 04 predicted that a systemd built without libseccomp would give one
///         answer for the whole seccomp-implemented family — either it refuses them all
///         or it ignores them all — and the measurement of 2026-08-25 found that it
///         gives <i>both</i>, split down the middle of that family.
///     </para>
///     <para>
///         ⚠ <b><see cref="Inert" /> is worse than <see cref="Rejected" />, and that
///         inversion is the whole point.</b> A refused property fails the D-Bus call:
///         the launch does not happen, somebody reads an error naming the property, and
///         the system is exactly as contained as it was before. An inert property is
///         accepted, is reported back by <c>systemctl show</c> byte-for-byte as it would
///         be on a machine that enforces it, and restricts nothing at all. Everything
///         that looks at the running system — an audit, a screenshot in a bug report,
///         a future reviewer — reads it as armed. Nothing distinguishes it from the
///         real thing except knowing how the binary was built, which is precisely the
///         fact that gets lost.
///     </para>
///     <para>
///         So the two must not be spellable as one <see langword="bool" />. A property
///         cannot be constructed without stating which of the three it is (see
///         <see cref="UnitProperty.Enforcement" />), and an <see cref="Inert" /> one is
///         additionally recorded in <see cref="SandboxUnit.Gaps" /> so that the launch
///         says out loud what it is not doing.
///     </para>
/// </remarks>
public enum PropertyEnforcement {
    /// <summary>
    ///     systemd will fail the method call if this property is set on a transient
    ///     unit. It is not emitted; a <see cref="SandboxGapKind.SystemCapability" /> gap
    ///     is recorded instead.
    /// </summary>
    /// <remarks>
    ///     ⚠ Measured, not inferred: <c>SystemCallFilter=@system-service</c> over the bus
    ///     answers <i>"Cannot set property SystemCallFilter, or unknown property"</i> on
    ///     the image as built on 2026-08-25. Emitting it yields no application, which is
    ///     loud and recoverable — and is why this is the <i>less</i> dangerous failure.
    /// </remarks>
    Rejected = 0,

    /// <summary>
    ///     ⚠ systemd accepts the property, reports it back, and enforces nothing.
    /// </summary>
    /// <remarks>
    ///     The dangerous state. See the type's own remarks.
    /// </remarks>
    Inert,

    /// <summary>
    ///     systemd accepts the property and nothing about how this build was configured
    ///     removes the mechanism behind it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not the same as "an escape test proved it".</b> Exactly one property has
    ///     been shown to bite on this image — <c>PrivateNetwork=yes</c>, where
    ///     <c>ip link show eth0</c> fails inside the unit and succeeds in the control,
    ///     which is what makes the measurement credible when it says the other three
    ///     do <i>not</i> bite. For the rest this value means the narrower and honest
    ///     claim: the call succeeds, and the machinery those properties are built on
    ///     (mount namespaces, capabilities, cgroups, <c>prctl</c>) is present in this
    ///     kernel and this systemd, whereas libseccomp demonstrably is not. Doc 16's
    ///     escape-test suite is what would upgrade the claim.
    /// </remarks>
    Enforced
}

/// <summary>
///     What the systemd on <i>this</i> machine can actually be asked for.
/// </summary>
/// <remarks>
///     <para>
///         <c>base/recipes/systemd/recipe.sh</c> builds systemd 257 with
///         <c>-Dseccomp=disabled</c>, and systemd implements five of doc 04's properties
///         as seccomp filters. That much was known by reading. What was not known — and
///         what doc 04 explicitly could not settle from a Mac — is what a
///         <i>transient</i> unit does with those five, because a property goes over
///         D-Bus rather than through a unit-file parser that shrugs at unknown keys.
///     </para>
///     <para>
///         ✅ Measured 2026-08-25 in a booted VM, as root, against the system manager.
///         The five do not behave alike:
///     </para>
///     <list type="table">
///         <item>
///             <term><c>SystemCallFilter=</c>, <c>SystemCallArchitectures=</c></term>
///             <description>
///                 the call fails — <see cref="PropertyEnforcement.Rejected" />
///             </description>
///         </item>
///         <item>
///             <term>
///                 <c>MemoryDenyWriteExecute=</c>, <c>RestrictRealtime=</c>,
///                 <c>RestrictSUIDSGID=</c>
///             </term>
///             <description>
///                 accepted, and enforcing nothing — <see cref="PropertyEnforcement.Inert" />
///             </description>
///         </item>
///     </list>
///     <para>
///         The split is not arbitrary, and the shape that explains it is worth writing
///         down because it predicts which side a future property lands on. The first two
///         are <i>values</i> — a filter set, an architecture list — that only exist as
///         bus properties when systemd was compiled with something to parse them into,
///         so without libseccomp there is no property to set. The other three are plain
///         booleans on the exec context: systemd stores them unconditionally and only
///         consults them at exec time, in the code path that installs a filter, which on
///         this build is not there. ⚠ That is the explanation the measurement is
///         consistent with rather than something read out of systemd's source, so treat
///         it as a prediction to check rather than as a fact.
///     </para>
///     <para>
///         ⚠ <b>Do not build a probe on <c>systemctl show -p</c>.</b> It answers
///         <c>SystemCallFilter=~</c> — a value, for the property the transient setter
///         rejects — because it only distinguishes properties systemd knows about from
///         ones it does not. A probe built on it concludes that all five are available
///         and is wrong about all five. <see cref="SystemdFeatures" /> reads the feature
///         string instead, which is the source that actually knows.
///     </para>
/// </remarks>
public sealed record SandboxCapabilities {
    /// <summary>
    ///     The properties a seccomp-less systemd <b>fails the call</b> for.
    /// </summary>
    /// <seealso cref="PropertyEnforcement.Rejected" />
    public static IReadOnlyList<string> RefusedWithoutSeccomp { get; } = [
        "SystemCallFilter",
        "SystemCallArchitectures"
    ];

    /// <summary>
    ///     ⚠ The properties a seccomp-less systemd <b>accepts and does not enforce</b>.
    /// </summary>
    /// <seealso cref="PropertyEnforcement.Inert" />
    public static IReadOnlyList<string> InertWithoutSeccomp { get; } = [
        "MemoryDenyWriteExecute",
        "RestrictRealtime",
        "RestrictSUIDSGID"
    ];

    /// <summary>
    ///     All five properties systemd implements with libseccomp and only with it.
    /// </summary>
    /// <remarks>
    ///     One list, in one place, because the alternative — the builder knowing which
    ///     properties are seccomp-implemented, and the capability type knowing it too —
    ///     is two lists that agree until somebody adds a sixth property to one of them.
    /// </remarks>
    public static IReadOnlyList<string> ImplementedWithSeccomp { get; } = [
        .. RefusedWithoutSeccomp,
        .. InertWithoutSeccomp
    ];

    /// <summary>
    ///     Doc 04's assumption: a systemd with every property it names, all of them
    ///     enforcing.
    /// </summary>
    /// <remarks>
    ///     The default, because the alternative is a library whose out-of-the-box
    ///     behaviour is the degraded one — and a degraded default is the kind of thing
    ///     that survives into a release because nothing ever failed.
    /// </remarks>
    public static SandboxCapabilities Designed { get; } = new();

    /// <summary>
    ///     What the image's systemd is, as the probe reads it today.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No longer the launcher's input.</b> This used to be hand-derived from
    ///     reading <c>base/recipes/systemd/recipe.sh</c>, which is a fact that goes stale
    ///     the first time somebody changes the recipe and does not change this file. The
    ///     launcher now calls <see cref="SystemdProbe.Capabilities" /> and asks the
    ///     binary that is actually going to run the unit. What this constant is for is
    ///     the other direction: it is the expected answer, and
    ///     <c>SystemdFeaturesTests</c> pins it against the feature string captured from
    ///     the image, so a recipe change that flips <c>-SECCOMP</c> to <c>+SECCOMP</c>
    ///     shows up as a failing test rather than as a silently different launch.
    /// </remarks>
    public static SandboxCapabilities TrinixToday { get; } = new() { Seccomp = false };

    /// <summary>
    ///     Was systemd built with libseccomp?
    /// </summary>
    /// <remarks>
    ///     One fact, because it <i>is</i> one fact — and
    ///     <see cref="EnforcementOf(string)" /> is where it turns into the two different
    ///     answers the five properties get. It does <b>not</b> gate
    ///     <c>NoNewPrivileges=</c>, which is a <c>prctl</c> and needs nothing, nor the
    ///     <c>ProtectKernel*</c> and <c>ProtectControlGroups=</c> family, which are mount
    ///     and capability work with a seccomp filter as a bonus rather than as their
    ///     mechanism.
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
    ///     any of that. ⚠ Deliberately <b>not</b> derived from
    ///     <see cref="Features" />'s version number: "systemd is new enough" and "we
    ///     have decided to take the risk" are different statements, and only the second
    ///     one should turn a property on.
    /// </remarks>
    public bool PrivatePids { get; init; }

    /// <summary>
    ///     The feature string this was derived from, when it was derived rather than
    ///     assumed.
    /// </summary>
    /// <remarks>
    ///     Carried so that a refusal can quote its evidence. "The syscall floor is off"
    ///     is an assertion; "the syscall floor is off because <c>systemctl --version</c>
    ///     says <c>-SECCOMP</c>" is an assertion somebody can check in one command.
    /// </remarks>
    public SystemdFeatures? Features { get; init; }

    /// <summary>
    ///     What a systemd that reports these features can be asked for.
    /// </summary>
    /// <param name="features">Parsed from <c>systemctl --version</c>.</param>
    /// <remarks>
    ///     ⚠ A feature the string does not mention at all is treated as absent, and that
    ///     is the safe direction rather than the pessimistic one. Assuming seccomp is
    ///     missing costs two properties that would have been enforced; assuming it is
    ///     present costs a launch that fails with a D-Bus error, and — far worse — three
    ///     properties recorded as enforcing when they are theatre.
    /// </remarks>
    public static SandboxCapabilities From(SystemdFeatures features) {
        ArgumentNullException.ThrowIfNull(features);

        return new SandboxCapabilities {
            Seccomp = features.Has(SystemdFeatures.Seccomp) ?? false,
            Features = features
        };
    }

    /// <summary>
    ///     What setting <paramref name="property" /> on a transient unit would actually
    ///     achieve here.
    /// </summary>
    /// <param name="property">A systemd property name, e.g. <c>RestrictRealtime</c>.</param>
    /// <remarks>
    ///     ⚠ Everything not in <see cref="ImplementedWithSeccomp" /> answers
    ///     <see cref="PropertyEnforcement.Enforced" />, including a property this type
    ///     has never heard of. That is deliberate: this type models one build-time
    ///     decision — libseccomp, in or out — and pretending to model every possible way
    ///     a systemd can be short of a property would be a lookup table nobody keeps
    ///     current. A property that turns out to need its own gate belongs in one of the
    ///     two lists above, with a measurement next to it.
    /// </remarks>
    public PropertyEnforcement EnforcementOf(string property) {
        if (Seccomp || !ImplementedWithSeccomp.Contains(property, StringComparer.Ordinal)) {
            return PropertyEnforcement.Enforced;
        }

        return RefusedWithoutSeccomp.Contains(property, StringComparer.Ordinal)
            ? PropertyEnforcement.Rejected
            : PropertyEnforcement.Inert;
    }
}

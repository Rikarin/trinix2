using System.Globalization;
using Trinix.Bundle;

namespace Trinix.Sandbox;

/// <summary>
///     One <c>Name=value</c> line of a transient unit.
/// </summary>
/// <remarks>
///     A record rather than a formatted string, so that a test can ask for
///     <c>PrivateNetwork</c> by name and get an answer instead of doing a substring
///     search over a rendered command line. The rendering is one method at the bottom
///     of <see cref="SandboxUnit" /> and it is the only place that knows what
///     <c>systemd-run</c>'s argument syntax looks like.
/// </remarks>
public sealed record UnitProperty {
    /// <summary>The systemd property name, e.g. <c>RootDirectory</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Its value, exactly as systemd should see it.</summary>
    public required string Value { get; init; }

    /// <summary>Which doc 04 table row this came from, for a reader and for a log.</summary>
    public required string Rationale { get; init; }

    /// <summary>
    ///     Whether setting this property achieves anything on the system the unit was
    ///     built for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Required, and that is the point of it.</b> This used to be a
    ///         <c>NeedsSeccomp</c> flag defaulting to <see langword="false" />, which
    ///         made "this property protects the application" the answer a caller got by
    ///         saying nothing — and the measurement of 2026-08-25 found three properties
    ///         for which that answer is wrong in the worst available way: accepted by
    ///         systemd, readable back from <c>systemctl show</c>, enforcing nothing.
    ///         Making the field required means a property cannot enter this list without
    ///         somebody stating which of the three states it is in, so
    ///         <see cref="PropertyEnforcement.Inert" /> can never be reached by
    ///         forgetting. <see cref="Enforced" /> is the shorthand for the ordinary
    ///         case, and it is a statement rather than a default.
    ///     </para>
    /// </remarks>
    public required PropertyEnforcement Enforcement { get; init; }

    /// <summary>
    ///     A property that does what it says on the system this unit is for.
    /// </summary>
    /// <param name="name">The systemd property name.</param>
    /// <param name="value">Its value, exactly as systemd should see it.</param>
    /// <param name="rationale">Which doc 04 table row it came from.</param>
    /// <remarks>
    ///     ⚠ Only for properties whose mechanism is not seccomp — see
    ///     <see cref="SandboxCapabilities.ImplementedWithSeccomp" /> for the five that
    ///     must go through <see cref="SandboxCapabilities.EnforcementOf(string)" />
    ///     instead. Using this for one of those would be asserting an enforcement that
    ///     was measured not to happen.
    /// </remarks>
    public static UnitProperty Enforced(string name, string value, string rationale) =>
        new() {
            Name = name,
            Value = value,
            Rationale = rationale,
            Enforcement = PropertyEnforcement.Enforced
        };
}

/// <summary>
///     Something doc 04 asks for that this unit does not deliver.
/// </summary>
/// <remarks>
///     <para>
///         The most useful thing this library produces, and the reason it produces a
///         structure rather than a command line. Doc 04's migration plan starts with
///         audit mode — "the units are constructed, and every violation is logged and
///         allowed" — and audit mode is only worth running if the thing doing the
///         logging knows what was <i>never enforced in the first place</i>. A
///         permission that has no systemd property, on a systemd that could not have
///         applied it anyway, is not a violation waiting to happen; it is a gap that
///         was there at launch and should be in the journal line that records the
///         launch.
///     </para>
///     <para>
///         ⚠ Most of the fourteen end up here, and that is the design rather than a
///         shortfall. Doc 04's containment is a <i>floor</i> — nothing, unless
///         declared — and its authority is a <i>broker</i>. Only four of the fourteen
///         say anything about a systemd property at all.
///     </para>
/// </remarks>
public sealed record SandboxGap {
    /// <summary>What is not enforced: a permission name, or a systemd property name.</summary>
    public required string Subject { get; init; }

    /// <summary>Who, or what, would have to enforce it.</summary>
    public required SandboxGapKind Kind { get; init; }

    /// <summary>One sentence a journal line can carry verbatim.</summary>
    public required string Reason { get; init; }
}

/// <summary>Why a <see cref="SandboxGap" /> is a gap.</summary>
public enum SandboxGapKind {
    /// <summary>
    ///     The broker mediates it, and the broker does not exist yet. The permission
    ///     is recorded and buys the right to ask; nothing yet answers.
    /// </summary>
    Broker,

    /// <summary>
    ///     The compositor mediates it. A bound Wayland socket is not a granted window.
    /// </summary>
    Compositor,

    /// <summary>
    ///     The session manager mediates it: whether a unit outlives its last window,
    ///     and whether it is started at login, is not an <c>ExecContext</c> property.
    /// </summary>
    SessionManager,

    /// <summary>
    ///     The property was omitted because this systemd would fail the call. See
    ///     <see cref="PropertyEnforcement.Rejected" />.
    /// </summary>
    SystemCapability,

    /// <summary>
    ///     ⚠ The property <b>was</b> set, systemd accepted it, and it enforces nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The most important line this library can put in a journal, and the one
    ///         doc 04 did not know it needed. A <see cref="SystemCapability" /> gap
    ///         describes a property that is absent, which is a state anybody can observe
    ///         from outside: <c>systemctl show</c> does not report it and the unit does
    ///         not carry it. This one describes a property that is present in every
    ///         observable sense and inert in the only sense that matters — so there is
    ///         no way to find it by looking at the running system, and it will read as
    ///         hardening to every audit that ever looks.
    ///     </para>
    ///     <para>
    ///         Which is why it is a gap at all. Doc 04: "a permission enforced by nothing
    ///         should be a line in the journal rather than a silence." Three properties
    ///         on the image as built today are exactly that, and this kind is how they
    ///         get their line.
    ///     </para>
    /// </remarks>
    Inert,

    /// <summary>
    ///     systemd has no property that expresses the distinction the permission
    ///     makes, so the coarser thing was applied instead.
    /// </summary>
    NoSuchProperty,

    /// <summary>
    ///     The code being run cannot tolerate the restriction. There is exactly one
    ///     of these — a JIT and <c>MemoryDenyWriteExecute=</c> — and doc 04 makes it
    ///     an explicit, per-bundle exception rather than a system-wide surrender.
    /// </summary>
    Runtime,

    /// <summary>
    ///     <c>Info.json</c> cannot express the fact the enforcement would need, so the
    ///     safe assumption was made instead.
    /// </summary>
    /// <remarks>
    ///     ⚠ The one that should shrink over time. A gap of this kind is a request for
    ///     a field in a future schema revision, and it is recorded rather than silently
    ///     assumed precisely so that the request is visible in a log instead of living
    ///     in somebody's head.
    /// </remarks>
    BundleFormat
}

/// <summary>
///     The transient unit an application is launched inside — as data.
/// </summary>
/// <remarks>
///     <para>
///         Doc 04: "Launching an application becomes: verify the bundle, <b>construct a
///         transient systemd unit</b> from its signed permissions, and start it there."
///         This type is the middle clause, and separating it from the last one is what
///         makes the security properties assertable. "A bundle with no
///         <c>network.client</c> gets <c>PrivateNetwork=yes</c>" is a statement about
///         this object; checking it by launching something and then trying to open a
///         socket would test the same claim through four more layers, in a VM, slowly.
///     </para>
///     <para>
///         ⚠ Transient, and never written to disk. Doc 04's reason is worth repeating
///         where the code is: "A unit file on disk is a permission grant that outlives
///         a re-signing, and one an attacker can edit." The unit is derived from the
///         signature at every launch, so re-signing an application with fewer
///         permissions takes effect the next time it starts and there is no stale copy
///         to find.
///     </para>
/// </remarks>
public sealed record SandboxUnit {
    /// <summary>The transient unit's name, ending in <c>.service</c>.</summary>
    public required string UnitName { get; init; }

    /// <summary>What <c>systemctl status</c> shows.</summary>
    public required string Description { get; init; }

    /// <summary>The absolute path to the executable, inside the container.</summary>
    public required string Program { get; init; }

    /// <summary>Arguments after <c>argv[0]</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The environment, as <c>NAME=value</c>.</summary>
    /// <remarks>
    ///     ⚠ The complete environment, not additions to an inherited one. A transient
    ///     unit started by <c>systemd-run</c> inherits the service manager's
    ///     environment, which on the system manager is nearly empty and on a user
    ///     manager is whatever the session put there — neither is a thing an
    ///     application's containment should depend on.
    /// </remarks>
    public IReadOnlyList<string> Environment { get; init; } = [];

    /// <summary>Every property, in the order doc 04's mapping table lists them.</summary>
    public IReadOnlyList<UnitProperty> Properties { get; init; } = [];

    /// <summary>What this unit does not enforce, and who would have to.</summary>
    public IReadOnlyList<SandboxGap> Gaps { get; init; } = [];

    /// <summary>What the bundle declared, parsed.</summary>
    public required PermissionSet Permissions { get; init; }

    /// <summary>What kind of code the entry point is, which decided <c>MemoryDenyWriteExecute=</c>.</summary>
    public required BundleRuntime Runtime { get; init; }

    /// <summary>The value of a property, or <see langword="null" /> if it is not set.</summary>
    /// <param name="name">The systemd property name.</param>
    /// <remarks>
    ///     ⚠ Returns the <i>first</i> match. Several properties are legitimately
    ///     repeated — <c>BindReadOnlyPaths=</c> is emitted once per path, because a
    ///     single comma-joined value would make one unreadable line out of five
    ///     readable ones and would hide which path a systemd error was about. Use
    ///     <see cref="ValuesOf" /> for those.
    /// </remarks>
    public string? ValueOf(string name) {
        foreach (var property in Properties) {
            if (string.Equals(property.Name, name, StringComparison.Ordinal)) {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>Every value set for a repeatable property, in order.</summary>
    /// <param name="name">The systemd property name.</param>
    public IReadOnlyList<string> ValuesOf(string name) =>
        [.. Properties.Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)).Select(p => p.Value)];

    /// <summary>Is this property set at all?</summary>
    /// <param name="name">The systemd property name.</param>
    /// <remarks>
    ///     ⚠ "Set" is not "enforced", and on the image as built today three properties
    ///     make that distinction real. Use <see cref="Enforces" /> for the question a
    ///     security assertion actually means.
    /// </remarks>
    public bool Sets(string name) => ValueOf(name) is not null;

    /// <summary>Is this property set <i>and</i> going to do something?</summary>
    /// <param name="name">The systemd property name.</param>
    public bool Enforces(string name) =>
        Properties.Any(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal)
            && p.Enforcement == PropertyEnforcement.Enforced
        );

    /// <summary>
    ///     ⚠ Every property this unit sets that enforces nothing.
    /// </summary>
    /// <remarks>
    ///     The theatre, listed. Each one also has a <see cref="SandboxGapKind.Inert" />
    ///     entry in <see cref="Gaps" /> when its value asserts a restriction — the two
    ///     views exist because a launcher wants the sentence and <c>trinix doctor</c>
    ///     wants the property.
    /// </remarks>
    public IReadOnlyList<UnitProperty> InertProperties =>
        [.. Properties.Where(p => p.Enforcement == PropertyEnforcement.Inert)];

    /// <summary>
    ///     The unit as <c>systemd-run</c> would be invoked to create it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Arguments, not a shell command line.</b> There is no quoting here
    ///         and there must never be any: the list is handed to <c>execve</c> as
    ///         <c>argv</c>, so a bundle whose name contains a space or a semicolon is
    ///         one argument either way. The moment this returns a string somebody will
    ///         paste it into a shell and the escaping will be wrong exactly once.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>--collect</c> so a unit that fails is garbage-collected rather than
    ///         left in a failed state that blocks the next launch under the same name.
    ///         ⚠ <c>--quiet</c> because <c>systemd-run</c>'s "Running as unit" line on
    ///         stderr is not something a person double-clicking an icon should see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Unverified.</b> That <c>systemd-run</c> 257 accepts every
    ///         <c>--property=</c> below, spelled this way, over the bus, is an
    ///         assumption. Nothing in this repository can check it, and the failure it
    ///         would produce — a launch that refuses with a D-Bus error naming a
    ///         property — is exactly what the wiring slice has to look for first.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> ToSystemdRunArguments() {
        List<string> arguments = [
            "--quiet",
            "--collect",
            "--unit=" + UnitName,
            "--description=" + Description
        ];

        foreach (var property in Properties) {
            arguments.Add($"--property={property.Name}={property.Value}");
        }

        foreach (var variable in Environment) {
            arguments.Add("--setenv=" + variable);
        }

        // "--" first, so an application argument that begins with a dash is the
        // application's problem and not systemd-run's.
        arguments.Add("--");
        arguments.Add(Program);
        arguments.AddRange(Arguments);
        return arguments;
    }

    /// <summary>
    ///     The unit as a <c>.service</c> file would spell it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>For a human and for a test, never for disk.</b> Nothing writes this
    ///     anywhere: doc 04 refuses a unit file on disk because it is a permission
    ///     grant that outlives a re-signing. What it is for is <c>trinix doctor</c> and
    ///     a bug report — "show me what this application would run as" is a question
    ///     with an answer, and the answer is much easier to read in the format everyone
    ///     already knows than as forty <c>--property=</c> arguments.
    ///
    ///     ⚠ An inert property is rendered with a comment above it saying so, which is
    ///     the one respect in which this is <i>not</i> what a unit file would look like.
    ///     That is deliberate: the reason somebody reads this output is to find out what
    ///     an application is contained by, and a line that answers that question wrongly
    ///     is worse than no output. The comment is also why this must never be written
    ///     to disk and then loaded back.
    /// </remarks>
    public string ToUnitFile() {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"[Unit]\nDescription={Description}\n\n[Service]\n");

        foreach (var property in Properties) {
            if (property.Enforcement == PropertyEnforcement.Inert) {
                text.Append("# ⚠ accepted by this systemd and enforcing nothing — see Gaps\n");
            }

            text.Append(CultureInfo.InvariantCulture, $"{property.Name}={property.Value}\n");
        }

        foreach (var variable in Environment) {
            text.Append(CultureInfo.InvariantCulture, $"Environment={variable}\n");
        }

        text.Append("ExecStart=").Append(Program);
        foreach (var argument in Arguments) {
            text.Append(' ').Append(argument);
        }

        return text.Append('\n').ToString();
    }
}

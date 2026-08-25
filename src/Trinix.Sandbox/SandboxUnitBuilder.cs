using System.Globalization;
using Trinix.Bundle;

namespace Trinix.Sandbox;

/// <summary>
///     Everything about a launch that is not the bundle and not the paths.
/// </summary>
public sealed record SandboxOptions {
    /// <summary>What this system's systemd can be asked for.</summary>
    public SandboxCapabilities Capabilities { get; init; } = SandboxCapabilities.Designed;

    /// <summary>The cgroup limits.</summary>
    public SandboxResourceBounds Resources { get; init; } = SandboxResourceBounds.Default;

    /// <summary>
    ///     What kind of code the entry point is. See <see cref="BundleRuntimeDetection" />.
    /// </summary>
    public BundleRuntime Runtime { get; init; } = BundleRuntime.Unknown;

    /// <summary>Arguments passed through to the application, after <c>argv[0]</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    ///     A token distinguishing this launch from another of the same application,
    ///     or <see langword="null" /> for one instance at a time.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see langword="null" /> by default, which means the unit is named after
    ///     the application and a second launch collides with the first. That is the
    ///     behaviour Trinix wants: on a Mac, opening an application that is already
    ///     open activates the window rather than starting a second copy, and a
    ///     one-unit-per-identity naming makes "is it running" a question
    ///     <c>systemctl</c> answers. The token exists for the cases that genuinely
    ///     want several — a terminal, a document-per-window editor — and it is the
    ///     caller's job to know which it is.
    /// </remarks>
    public string? Instance { get; init; }

    /// <summary>The Wayland socket's name, for <c>WAYLAND_DISPLAY</c>.</summary>
    public string WaylandDisplay { get; init; } = SandboxLayout.DefaultWaylandDisplay;
}

/// <summary>
///     Turns a verified bundle and a resolved layout into the transient unit it runs
///     in.
/// </summary>
/// <remarks>
///     <para>
///         A pure function, and deliberately so: no filesystem, no clock, no
///         randomness, no environment. Everything it needs is in its arguments, which
///         is what makes the interesting claims — a bundle with no
///         <c>network.client</c> gets <c>PrivateNetwork=yes</c>; a bundle with
///         <c>files.home</c> gets no bind mount of the user's home — assertable in a
///         test that runs anywhere.
///     </para>
///     <para>
///         ⚠ <b>The floor is unconditional and the permissions only ever add.</b>
///         Every property in <see cref="Floor" /> is emitted for every application,
///         and the four permissions that touch the unit at all can turn something
///         <i>on</i>. There is no path through this code where declaring a permission
///         removes a restriction that would otherwise apply — which is what makes the
///         worst case of a mis-parsed manifest "the application is more contained than
///         intended" rather than the other way round.
///     </para>
/// </remarks>
public static class SandboxUnitBuilder {
    /// <summary>The prefix every application's transient unit carries.</summary>
    /// <remarks>
    ///     ⚠ Load-bearing beyond cosmetics. Doc 04's broker "decides who is asking from
    ///     the caller's cgroup, which maps to the transient unit, which maps to a
    ///     verified bundle identity" — so the unit name is the attribution channel, and
    ///     a prefix that nothing else in the system uses is what lets the broker tell a
    ///     contained application from a process started at a terminal.
    /// </remarks>
    public const string UnitPrefix = "trinix-app-";

    /// <summary>
    ///     Build the unit, or refuse.
    /// </summary>
    /// <param name="info">The bundle's <c>Info.json</c>, already verified.</param>
    /// <param name="layout">Resolved paths for this application and this user.</param>
    /// <param name="options">Capabilities, bounds, runtime kind and arguments.</param>
    /// <exception cref="BundleException">
    ///     <see cref="BundleFailure.UnknownPermission" /> when the manifest declares a
    ///     permission this system does not define, or
    ///     <see cref="BundleFailure.MalformedInfo" /> when the entry point is not a
    ///     safe bundle-relative path or the layout is not made of absolute paths.
    /// </exception>
    public static SandboxUnit Build(BundleInfo info, SandboxLayout layout, SandboxOptions? options = null) {
        options ??= new SandboxOptions();

        // ⚠ Parsed before anything else is decided, and it throws. An unknown
        // permission is a refusal to launch: see PermissionSet's remarks for why
        // both alternatives — dropping it, or approximating it — are worse.
        var permissions = PermissionSet.Parse(info);

        var layoutProblems = layout.Validate();
        if (layoutProblems.Count > 0) {
            throw new BundleException(
                BundleFailure.MalformedInfo,
                "cannot build a sandbox for " + info.Identifier + ": " + string.Join("; ", layoutProblems)
            );
        }

        // Checked again here even though BundleVerifier already checked it. A
        // verified signature proves a developer signed the manifest, not that the
        // developer was careful, and this is the value that becomes an absolute
        // path in ExecStart=.
        if (!BundleLayout.IsSafeRelativePath(info.EntryPoint)) {
            throw new BundleException(
                BundleFailure.MalformedInfo,
                $"entryPoint '{info.EntryPoint}' is not a safe bundle-relative path"
            );
        }

        List<UnitProperty> properties = [];
        List<SandboxGap> gaps = [];

        Identity(properties, layout);
        Filesystem(properties, layout);
        Network(properties, layout, permissions, gaps);
        Devices(properties);
        Processes(properties, options.Capabilities);
        Floor(properties, options, gaps);
        Kernel(properties, options.Capabilities, gaps);
        Bounds(properties, options.Resources);

        if (permissions.Has(Permissions.Display)) {
            // ⚠ BindPaths, not BindReadOnlyPaths. Connecting to a unix socket needs
            // write permission on its inode, so a read-only bind of the Wayland
            // socket produces an application that starts, finds the socket, and gets
            // EACCES from connect() — which reads as a compositor bug.
            properties.Add(UnitProperty.Enforced(
                "BindPaths",
                layout.WaylandSocket + ":" + layout.WaylandSocket,
                "display: the compositor's socket, at the same path inside"
            ));
        }

        Delegated(permissions, gaps);

        return new SandboxUnit {
            UnitName = UnitNameFor(info.Identifier, options.Instance),
            Description = $"{info.Name} ({info.Identifier})",
            Program = layout.BundlePath + "/" + info.EntryPoint,
            Arguments = [.. options.Arguments],
            Environment = EnvironmentFor(info, layout, permissions, options),
            Properties = properties,
            Gaps = gaps,
            Permissions = permissions,
            Runtime = options.Runtime
        };
    }

    /// <summary>The transient unit's name for an application.</summary>
    /// <param name="identifier">The bundle identifier, which is already reverse-DNS.</param>
    /// <param name="instance">A distinguishing token, or <see langword="null" />.</param>
    public static string UnitNameFor(string identifier, string? instance = null) =>
        instance is null
            ? UnitPrefix + identifier + ".service"
            : UnitPrefix + identifier + "-" + instance + ".service";

    // --- the mapping table, one method per row -------------------------------

    static void Identity(List<UnitProperty> properties, SandboxLayout layout) {
        // ⚠ Not DynamicUser=yes, which every other Trinix unit uses. A dynamic uid
        // changes between launches, so everything the application wrote last time
        // would belong to a user that no longer exists. Containment here is the
        // mount namespace's job, not the uid's.
        properties.Add(UnitProperty.Enforced("User", layout.UserName, "the logged-in user, not a dynamic one"));
        properties.Add(UnitProperty.Enforced("Group", layout.GroupName, "the logged-in user, not a dynamic one"));

        // Type=exec rather than simple: systemd reports the unit started only once
        // the exec succeeded, so a bundle whose entry point is missing or is not
        // executable fails the launch instead of producing a unit that was briefly
        // alive and is now gone.
        properties.Add(UnitProperty.Enforced("Type", "exec", "a failed exec must be a failed launch"));
    }

    static void Filesystem(List<UnitProperty> properties, SandboxLayout layout) {
        properties.Add(UnitProperty.Enforced(
            "RootDirectory",
            layout.ComposedRoot,
            "cannot see the filesystem: a minimal composed root"
        ));

        // With RootDirectory= there is no /proc, /sys or /dev unless this asks for
        // them, and a .NET process without /proc does not get far — the GC reads
        // /proc/meminfo and the runtime reads /proc/self/maps.
        properties.Add(UnitProperty.Enforced(
            "MountAPIVFS",
            "yes",
            "/proc, /sys and /dev inside the composed root"
        ));

        properties.Add(UnitProperty.Enforced(
            "WorkingDirectory",
            layout.HomeInside,
            "an application's working directory is its own container"
        ));

        // /run has to exist and be writable for XDG_RUNTIME_DIR, and it must not be
        // the host's — that is where every other service's socket lives.
        properties.Add(UnitProperty.Enforced(
            "TemporaryFileSystem",
            "/run:mode=0755",
            "an empty /run, so no other service's socket is reachable"
        ));

        properties.Add(UnitProperty.Enforced(
            "PrivateTmp",
            "yes",
            "/tmp and /var/tmp are the application's alone"
        ));

        foreach (var path in layout.RuntimePaths) {
            properties.Add(UnitProperty.Enforced(
                "BindReadOnlyPaths",
                path,
                "the runtime: not a permission, a fact about a process"
            ));
        }

        properties.Add(UnitProperty.Enforced(
            "BindReadOnlyPaths",
            layout.BundlePath,
            "the bundle, read-only and at the same path inside"
        ));

        // The single largest simplification in doc 04: the app's $HOME *is* its
        // container, so every library that writes to $HOME/.config writes into
        // ~/Library/Containers/<id>/Data and nothing has to be patched.
        properties.Add(UnitProperty.Enforced(
            "BindPaths",
            layout.ContainerData + ":" + layout.HomeInside,
            "the app's $HOME is its container"
        ));
    }

    static void Network(
        List<UnitProperty> properties,
        SandboxLayout layout,
        PermissionSet permissions,
        List<SandboxGap> gaps
    ) {
        if (!permissions.HasAny(Permissions.NetworkClient | Permissions.NetworkServer)) {
            // ✅ The one property on this image whose enforcement was proven rather
            // than assumed: `ip link show eth0` fails inside the unit and succeeds in
            // the control. That control is what makes the same harness credible when
            // it reports that RestrictRealtime= does nothing.
            properties.Add(UnitProperty.Enforced(
                "PrivateNetwork",
                "yes",
                "no network permission: no network stack at all"
            ));

            return;
        }

        // ⚠ NetworkNamespacePath= rather than PrivateNetwork=no. The difference is
        // that the application joins one namespace shared by applications that have
        // the permission, instead of the host's — so it cannot see the compositor's
        // abstract sockets or anything else bound in the root namespace.
        //
        // ⚠ Assumed, not verified: systemd.exec says PrivateNetwork= has no effect
        // when NetworkNamespacePath= is set, so PrivateNetwork= is deliberately not
        // emitted alongside it rather than emitted as "no". If that reading is wrong
        // the failure is loud (systemd refuses the combination), not silent.
        properties.Add(UnitProperty.Enforced(
            "NetworkNamespacePath",
            layout.NetworkNamespace,
            "network.*: a namespace shared only by applications that declared it"
        ));

        if (permissions.Has(Permissions.NetworkServer)) {
            gaps.Add(new SandboxGap {
                Subject = BundlePermissions.NetworkServer,
                Kind = SandboxGapKind.NoSuchProperty,
                Reason = "systemd has no property that separates listening from connecting; the shared "
                    + "namespace grants both, and an application with only network.client is not "
                    + "prevented from listening inside it"
            });
        }
    }

    static void Devices(List<UnitProperty> properties) {
        properties.Add(UnitProperty.Enforced(
            "PrivateDevices",
            "yes",
            "no devices: everything arrives as an fd from trinixd"
        ));

        // Doc 04's "DeviceAllow= empty" is spelled as the absence of any DeviceAllow
        // line: with DevicePolicy=closed the only nodes reachable are the harmless
        // set systemd always permits (null, zero, full, random, urandom, tty), and
        // an explicit empty DeviceAllow= over the bus is a value systemd-run may
        // well reject.
        properties.Add(UnitProperty.Enforced("DevicePolicy", "closed", "no devices beyond the harmless set"));
    }

    static void Processes(List<UnitProperty> properties, SandboxCapabilities capabilities) {
        properties.Add(UnitProperty.Enforced("ProtectProc", "invisible", "no other processes"));

        // ⚠ ProcSubset=pid is the documented companion and is deliberately NOT set.
        // It hides /proc/meminfo, /proc/cpuinfo and /proc/sys, all of which the .NET
        // GC reads to size itself — the result is not a refusal but an application
        // that runs with wrong memory heuristics, which is the kind of bug nobody
        // traces back to a sandbox property.
        if (capabilities.PrivatePids) {
            properties.Add(UnitProperty.Enforced("PrivatePIDs", "yes", "no other processes, in the strong form"));
        }
    }

    static void Floor(List<UnitProperty> properties, SandboxOptions options, List<SandboxGap> gaps) {
        // NoNewPrivileges is a prctl and needs no libseccomp, which is why it is
        // here unconditionally while its neighbours are not. It is also the single
        // most valuable property in the row, so losing it alongside them — the easy
        // mistake — would cost more than the two that are lost.
        properties.Add(UnitProperty.Enforced(
            "NoNewPrivileges",
            "yes",
            "syscall floor: no setuid binary can raise privilege"
        ));

        Filtered(
            properties, options.Capabilities, gaps,
            "SystemCallFilter", "@system-service", "syscall floor",
            "the transient setter answers \"Cannot set property SystemCallFilter, or unknown property\""
        );

        Filtered(
            properties, options.Capabilities, gaps,
            "SystemCallArchitectures", "native", "syscall floor",
            "the transient setter fails the call, exactly as it does for SystemCallFilter"
        );

        // ⚠ Doc 04's per-bundle exception. Off for a JIT, because the runtime maps a
        // page writable, writes machine code and remaps it executable — which is
        // exactly what this forbids — and off for Unknown, because the detector is
        // only allowed to be wrong in the direction that does not crash the
        // application on its first compiled method.
        var deny = options.Runtime == BundleRuntime.Native;
        Filtered(
            properties, options.Capabilities, gaps,
            "MemoryDenyWriteExecute", deny ? "yes" : "no", "syscall floor: W^X, where the runtime allows it",

            // ⚠ The one of the three whose inertness was inferred rather than seen.
            // The measurement had no W+X program in the image to try, so the evidence
            // is by neighbourhood: it is the same seccomp mechanism as the two that
            // were watched failing to bite. Saying so is the difference between a
            // record and a guess wearing a record's clothes.
            "untested directly — the image has no program that maps W+X — but it is the same seccomp "
            + "mechanism as RestrictRealtime= and RestrictSUIDSGID=, both of which were measured inert",

            // ⚠ …and only theatre when it claims something. MemoryDenyWriteExecute=no
            // restricts nothing by design, so recording it as an unenforced restriction
            // would put a line in the journal that is true of every correct system.
            asserts: deny
        );

        if (deny) {
            return;
        }

        gaps.Add(new SandboxGap {
            Subject = "MemoryDenyWriteExecute",
            Kind = options.Runtime == BundleRuntime.Managed ? SandboxGapKind.Runtime : SandboxGapKind.BundleFormat,
            Reason = options.Runtime == BundleRuntime.Managed
                ? "off by design: the .NET JIT needs W^X transitions, and doc 04 records the exception "
                + "per bundle rather than surrendering it system-wide"
                : "off because nothing said otherwise: Info.json cannot declare whether the entry point "
                + "is NativeAOT, and inferring it from an absence would crash a JIT at its first "
                + "compiled method rather than at startup"
        });
    }

    static void Kernel(List<UnitProperty> properties, SandboxCapabilities capabilities, List<SandboxGap> gaps) {
        // Mount and capability work, with a seccomp filter as a bonus rather than as
        // the mechanism — so these are emitted whether or not systemd has libseccomp.
        properties.Add(UnitProperty.Enforced("ProtectKernelTunables", "yes", "kernel surfaces"));
        properties.Add(UnitProperty.Enforced("ProtectKernelModules", "yes", "kernel surfaces"));
        properties.Add(UnitProperty.Enforced("ProtectControlGroups", "yes", "kernel surfaces"));

        Filtered(
            properties, capabilities, gaps,
            "RestrictRealtime", "yes", "kernel surfaces",
            "`chrt -r 1` succeeds inside a unit that sets it"
        );

        Filtered(
            properties, capabilities, gaps,
            "RestrictSUIDSGID", "yes", "kernel surfaces",
            "`chmod u+s` inside a unit that sets it leaves the file at mode 4644"
        );
    }

    static void Bounds(List<UnitProperty> properties, SandboxResourceBounds bounds) {
        properties.Add(UnitProperty.Enforced(
            "MemoryMax",
            bounds.MemoryMax,
            "resource bounds, and doc 11's System Monitor"
        ));

        properties.Add(UnitProperty.Enforced(
            "CPUWeight",
            bounds.CpuWeight.ToString(CultureInfo.InvariantCulture),
            "resource bounds, and doc 11's System Monitor"
        ));

        properties.Add(UnitProperty.Enforced(
            "TasksMax",
            bounds.TasksMax.ToString(CultureInfo.InvariantCulture),
            "resource bounds, and doc 11's System Monitor"
        ));
    }

    /// <summary>
    ///     Emit one of the five properties systemd implements as a seccomp filter,
    ///     according to what this build actually does with it.
    /// </summary>
    /// <param name="properties">The unit under construction.</param>
    /// <param name="capabilities">What this systemd can be asked for.</param>
    /// <param name="gaps">Where an unenforced property is recorded.</param>
    /// <param name="name">The systemd property name.</param>
    /// <param name="value">Its value.</param>
    /// <param name="rationale">Which doc 04 table row it came from.</param>
    /// <param name="evidence">
    ///     What the measurement of 2026-08-25 saw this property do — quoted into the
    ///     gap, so a journal line carries its own proof rather than an assertion.
    /// </param>
    /// <param name="asserts">
    ///     Whether <paramref name="value" /> claims a restriction at all. Only a value
    ///     that claims one can be theatre.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The inert branch emits the property, and that is a decision rather
    ///         than an oversight.</b> Withholding it would buy nothing — the enforcement
    ///         is absent either way, since it is absent from the binary — while costing
    ///         two things worth having: a unit that becomes correct the moment somebody
    ///         adds the <c>libseccomp</c> recipe, with no code change and nothing to
    ///         remember; and a <c>systemctl show</c> that agrees with what the launcher
    ///         intended, so a divergence between the two means something.
    ///     </para>
    ///     <para>
    ///         What withholding it would <i>look</i> like buying is honesty, and that is
    ///         the trap: the dishonesty is not in the property, it is in the silence
    ///         around it. So the property goes in and the silence is what gets fixed —
    ///         <see cref="SandboxGapKind.Inert" />, one line, in the journal at every
    ///         launch.
    ///     </para>
    ///     <para>
    ///         ⚠ The rejected branch does <b>not</b> emit, and must not: this is the one
    ///         of the three states where emitting produces no application at all. Doc 04
    ///         called that the worse outcome and the measurement showed it is the better
    ///         one — a failed launch names its property in a D-Bus error, which is a
    ///         thing somebody fixes.
    ///     </para>
    /// </remarks>
    static void Filtered(
        List<UnitProperty> properties,
        SandboxCapabilities capabilities,
        List<SandboxGap> gaps,
        string name,
        string value,
        string rationale,
        string evidence,
        bool asserts = true
    ) {
        var enforcement = capabilities.EnforcementOf(name);

        if (enforcement == PropertyEnforcement.Rejected) {
            gaps.Add(new SandboxGap {
                Subject = name,
                Kind = SandboxGapKind.SystemCapability,
                Reason = "omitted, because setting it would fail the launch: this systemd was built "
                    + "without libseccomp (base/recipes/systemd/recipe.sh) and " + evidence
                    + ". The application runs with no syscall floor and is no less contained than it "
                    + "was before this library existed"
            });

            return;
        }

        properties.Add(new UnitProperty {
            Name = name, Value = value, Rationale = rationale, Enforcement = enforcement
        });

        if (enforcement != PropertyEnforcement.Inert || !asserts) {
            return;
        }

        gaps.Add(new SandboxGap {
            Subject = name,
            Kind = SandboxGapKind.Inert,
            Reason = "set to '" + value + "' and enforcing nothing: this systemd was built without "
                + "libseccomp (base/recipes/systemd/recipe.sh), it accepts the property anyway, and "
                + "`systemctl show` reads it back exactly as it would on a system that enforces it — "
                + evidence
        });
    }

    /// <summary>
    ///     Record the permissions no unit property can enforce — which is most of them.
    /// </summary>
    /// <remarks>
    ///     ⚠ Ten of the fourteen land here, and reading that as a shortfall would be
    ///     reading doc 04 backwards. The unit's job is the floor: nothing, unless
    ///     stated. Authority above the floor is the broker's, and the broker's central
    ///     mechanism is that the user's choice <i>is</i> the grant — the application
    ///     gets an fd for the file the user picked, not a mount it can walk. A
    ///     permission with no property is not an unguarded permission; it is one whose
    ///     guard is a program rather than a namespace.
    /// </remarks>
    static void Delegated(PermissionSet permissions, List<SandboxGap> gaps) {
        foreach (var permission in permissions.Enumerate()) {
            var name = BundlePermissions.NameOf(permission);

            switch (permission) {
                case Permissions.Display:
                    gaps.Add(new SandboxGap {
                        Subject = name,
                        Kind = SandboxGapKind.Compositor,
                        Reason = "the unit binds the Wayland socket; whether a surface is accepted, and "
                            + "whether a consent dialog can be anchored to it, is the compositor's"
                    });

                    break;

                case Permissions.NetworkClient:
                case Permissions.NetworkServer:
                    // Enforced, in the only way systemd can: the namespace.
                    break;

                case Permissions.FilesHome:
                case Permissions.FilesRemovable:
                    gaps.Add(new SandboxGap {
                        Subject = name,
                        Kind = SandboxGapKind.Broker,
                        Reason = "weak by default and strong by use: the grant is the user picking a file "
                            + "in the shell's chooser and the broker passing back an fd, so there is "
                            + "deliberately no mount for this and nothing yet answers the request"
                    });

                    break;

                case Permissions.SystemBackground:
                    gaps.Add(new SandboxGap {
                        Subject = name,
                        Kind = SandboxGapKind.SessionManager,
                        Reason = "whether a unit outlives its last window, and whether it is started at "
                            + "login, is a session-manager policy rather than an exec property"
                    });

                    break;

                default:
                    gaps.Add(new SandboxGap {
                        Subject = name,
                        Kind = SandboxGapKind.Broker,
                        Reason = "broker-mediated: declaring it buys the right to ask, and the thing that "
                            + "asks the user and says no is trinix-broker, which does not exist yet"
                    });

                    break;
            }
        }
    }

    static List<string> EnvironmentFor(
        BundleInfo info,
        SandboxLayout layout,
        PermissionSet permissions,
        SandboxOptions options
    ) {
        // ⚠ No PATH, deliberately. The container has no /usr/bin, so there is nothing
        // for a PATH to find; an application that needs a helper program ships it in
        // Contents/Bin and names it. Setting a PATH that resolves to nothing would
        // turn "this feature was never going to work here" into a runtime mystery.
        List<string> environment = [
            "HOME=" + layout.HomeInside,
            "USER=" + layout.UserName,
            "LOGNAME=" + layout.UserName,
            "XDG_RUNTIME_DIR=" + layout.RuntimeDirectory,

            // The three the launcher already sets today, unchanged — which is what
            // makes the bundle findable the same way from managed code, a shell
            // script or a native binary. They keep their host values because the
            // bundle is bound in at the same path.
            "TRINIX_BUNDLE=" + layout.BundlePath,
            "TRINIX_BUNDLE_IDENTIFIER=" + info.Identifier,
            "TRINIX_BUNDLE_RESOURCES=" + layout.BundlePath + "/" + BundleLayout.ResourcesDirectory,

            "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "DOTNET_NOLOGO=1"
        ];

        if (permissions.Has(Permissions.Display)) {
            environment.Add("WAYLAND_DISPLAY=" + options.WaylandDisplay);
        }

        return environment;
    }
}

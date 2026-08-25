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
            properties.Add(new UnitProperty {
                Name = "BindPaths",
                Value = layout.WaylandSocket + ":" + layout.WaylandSocket,
                Rationale = "display: the compositor's socket, at the same path inside"
            });
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
        properties.Add(new UnitProperty {
            Name = "User", Value = layout.UserName, Rationale = "the logged-in user, not a dynamic one"
        });

        properties.Add(new UnitProperty {
            Name = "Group", Value = layout.GroupName, Rationale = "the logged-in user, not a dynamic one"
        });

        // Type=exec rather than simple: systemd reports the unit started only once
        // the exec succeeded, so a bundle whose entry point is missing or is not
        // executable fails the launch instead of producing a unit that was briefly
        // alive and is now gone.
        properties.Add(new UnitProperty {
            Name = "Type", Value = "exec", Rationale = "a failed exec must be a failed launch"
        });
    }

    static void Filesystem(List<UnitProperty> properties, SandboxLayout layout) {
        properties.Add(new UnitProperty {
            Name = "RootDirectory",
            Value = layout.ComposedRoot,
            Rationale = "cannot see the filesystem: a minimal composed root"
        });

        // With RootDirectory= there is no /proc, /sys or /dev unless this asks for
        // them, and a .NET process without /proc does not get far — the GC reads
        // /proc/meminfo and the runtime reads /proc/self/maps.
        properties.Add(new UnitProperty {
            Name = "MountAPIVFS", Value = "yes", Rationale = "/proc, /sys and /dev inside the composed root"
        });

        properties.Add(new UnitProperty {
            Name = "WorkingDirectory",
            Value = layout.HomeInside,
            Rationale = "an application's working directory is its own container"
        });

        // /run has to exist and be writable for XDG_RUNTIME_DIR, and it must not be
        // the host's — that is where every other service's socket lives.
        properties.Add(new UnitProperty {
            Name = "TemporaryFileSystem",
            Value = "/run:mode=0755",
            Rationale = "an empty /run, so no other service's socket is reachable"
        });

        properties.Add(new UnitProperty {
            Name = "PrivateTmp", Value = "yes", Rationale = "/tmp and /var/tmp are the application's alone"
        });

        foreach (var path in layout.RuntimePaths) {
            properties.Add(new UnitProperty {
                Name = "BindReadOnlyPaths", Value = path, Rationale = "the runtime: not a permission, a fact about a process"
            });
        }

        properties.Add(new UnitProperty {
            Name = "BindReadOnlyPaths",
            Value = layout.BundlePath,
            Rationale = "the bundle, read-only and at the same path inside"
        });

        // The single largest simplification in doc 04: the app's $HOME *is* its
        // container, so every library that writes to $HOME/.config writes into
        // ~/Library/Containers/<id>/Data and nothing has to be patched.
        properties.Add(new UnitProperty {
            Name = "BindPaths",
            Value = layout.ContainerData + ":" + layout.HomeInside,
            Rationale = "the app's $HOME is its container"
        });
    }

    static void Network(
        List<UnitProperty> properties,
        SandboxLayout layout,
        PermissionSet permissions,
        List<SandboxGap> gaps
    ) {
        if (!permissions.HasAny(Permissions.NetworkClient | Permissions.NetworkServer)) {
            properties.Add(new UnitProperty {
                Name = "PrivateNetwork", Value = "yes", Rationale = "no network permission: no network stack at all"
            });

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
        properties.Add(new UnitProperty {
            Name = "NetworkNamespacePath",
            Value = layout.NetworkNamespace,
            Rationale = "network.*: a namespace shared only by applications that declared it"
        });

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
        properties.Add(new UnitProperty {
            Name = "PrivateDevices", Value = "yes", Rationale = "no devices: everything arrives as an fd from trinixd"
        });

        // Doc 04's "DeviceAllow= empty" is spelled as the absence of any DeviceAllow
        // line: with DevicePolicy=closed the only nodes reachable are the harmless
        // set systemd always permits (null, zero, full, random, urandom, tty), and
        // an explicit empty DeviceAllow= over the bus is a value systemd-run may
        // well reject.
        properties.Add(new UnitProperty {
            Name = "DevicePolicy", Value = "closed", Rationale = "no devices beyond the harmless set"
        });
    }

    static void Processes(List<UnitProperty> properties, SandboxCapabilities capabilities) {
        properties.Add(new UnitProperty {
            Name = "ProtectProc", Value = "invisible", Rationale = "no other processes"
        });

        // ⚠ ProcSubset=pid is the documented companion and is deliberately NOT set.
        // It hides /proc/meminfo, /proc/cpuinfo and /proc/sys, all of which the .NET
        // GC reads to size itself — the result is not a refusal but an application
        // that runs with wrong memory heuristics, which is the kind of bug nobody
        // traces back to a sandbox property.
        if (capabilities.PrivatePids) {
            properties.Add(new UnitProperty {
                Name = "PrivatePIDs", Value = "yes", Rationale = "no other processes, in the strong form"
            });
        }
    }

    static void Floor(List<UnitProperty> properties, SandboxOptions options, List<SandboxGap> gaps) {
        // NoNewPrivileges is a prctl and needs no libseccomp, which is why it is
        // here unconditionally while its neighbours are not.
        properties.Add(new UnitProperty {
            Name = "NoNewPrivileges", Value = "yes", Rationale = "syscall floor: no setuid binary can raise privilege"
        });

        Seccomp(properties, options.Capabilities, gaps, "SystemCallFilter", "@system-service", "syscall floor");
        Seccomp(properties, options.Capabilities, gaps, "SystemCallArchitectures", "native", "syscall floor");

        // ⚠ Doc 04's per-bundle exception. Off for a JIT, because the runtime maps a
        // page writable, writes machine code and remaps it executable — which is
        // exactly what this forbids — and off for Unknown, because the detector is
        // only allowed to be wrong in the direction that does not crash the
        // application on its first compiled method.
        var deny = options.Runtime == BundleRuntime.Native;
        Seccomp(
            properties,
            options.Capabilities,
            gaps,
            "MemoryDenyWriteExecute",
            deny ? "yes" : "no",
            "syscall floor: W^X, where the runtime allows it"
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
        properties.Add(new UnitProperty {
            Name = "ProtectKernelTunables", Value = "yes", Rationale = "kernel surfaces"
        });

        properties.Add(new UnitProperty {
            Name = "ProtectKernelModules", Value = "yes", Rationale = "kernel surfaces"
        });

        properties.Add(new UnitProperty {
            Name = "ProtectControlGroups", Value = "yes", Rationale = "kernel surfaces"
        });

        Seccomp(properties, capabilities, gaps, "RestrictRealtime", "yes", "kernel surfaces");
        Seccomp(properties, capabilities, gaps, "RestrictSUIDSGID", "yes", "kernel surfaces");
    }

    static void Bounds(List<UnitProperty> properties, SandboxResourceBounds bounds) {
        properties.Add(new UnitProperty {
            Name = "MemoryMax", Value = bounds.MemoryMax, Rationale = "resource bounds, and doc 11's System Monitor"
        });

        properties.Add(new UnitProperty {
            Name = "CPUWeight",
            Value = bounds.CpuWeight.ToString(CultureInfo.InvariantCulture),
            Rationale = "resource bounds, and doc 11's System Monitor"
        });

        properties.Add(new UnitProperty {
            Name = "TasksMax",
            Value = bounds.TasksMax.ToString(CultureInfo.InvariantCulture),
            Rationale = "resource bounds, and doc 11's System Monitor"
        });
    }

    /// <summary>
    ///     Emit a property that systemd only implements when it was built with
    ///     libseccomp, or record its absence.
    /// </summary>
    static void Seccomp(
        List<UnitProperty> properties,
        SandboxCapabilities capabilities,
        List<SandboxGap> gaps,
        string name,
        string value,
        string rationale
    ) {
        if (capabilities.Seccomp) {
            properties.Add(new UnitProperty {
                Name = name, Value = value, Rationale = rationale, NeedsSeccomp = true
            });

            return;
        }

        gaps.Add(new SandboxGap {
            Subject = name,
            Kind = SandboxGapKind.SystemCapability,
            Reason = "omitted: this systemd was built without libseccomp, which is how it implements "
                + "this property (see base/recipes/systemd/recipe.sh)"
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

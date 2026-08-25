using Trinix.Bundle;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     The containment rules from doc 04's mapping table, one assertion each.
/// </summary>
/// <remarks>
///     <para>
///         These are the security properties stated directly rather than observed
///         through a launch. "An application with no <c>network.client</c> cannot open
///         a socket" is, underneath, "the unit sets <c>PrivateNetwork=yes</c>" —
///         everything after that is the kernel's business and is not Trinix's to test.
///         Asserting the property is therefore not a weaker test than launching
///         something and trying to connect; it is the same test with four fewer layers
///         and no VM.
///     </para>
///     <para>
///         ⚠ It is a weaker test in exactly one respect, and it is worth naming: these
///         assertions cannot tell a correct property from a plausible misspelling.
///         <c>PrivateNetworking=yes</c> would pass every case here and be silently
///         ignored by a real systemd. The gate for that is a booted system, and it is
///         the first thing the wiring slice owes.
///     </para>
/// </remarks>
public class SandboxUnitBuilderTests {
    // --- network -------------------------------------------------------------

    [Fact]
    public void AnApplicationThatDeclaredNoNetworkGetsNoNetworkStack() {
        var unit = TestSandbox.Unit();

        Assert.Equal("yes", unit.ValueOf("PrivateNetwork"));
        Assert.False(unit.Sets("NetworkNamespacePath"));
    }

    [Fact]
    public void DisplayAloneStillGetsNoNetworkStack() {
        // The shipped HelloUi bundle, exactly: a window and nothing else. It is
        // worth its own case because "declares a permission" and "declares a
        // network permission" are easy to conflate in a builder.
        var unit = TestSandbox.Unit(BundlePermissions.Display);

        Assert.Equal("yes", unit.ValueOf("PrivateNetwork"));
    }

    [Fact]
    public void NetworkClientJoinsTheSharedNamespaceInsteadOfThePrivateOne() {
        var unit = TestSandbox.Unit(BundlePermissions.NetworkClient);

        Assert.Equal(SandboxLayout.DefaultNetworkNamespace, unit.ValueOf("NetworkNamespacePath"));

        // ⚠ Not "PrivateNetwork=no". systemd.exec says PrivateNetwork= has no
        // effect once NetworkNamespacePath= is set, so emitting it would be a line
        // that looks like it grants the host's network and does not.
        Assert.False(unit.Sets("PrivateNetwork"));
    }

    [Fact]
    public void NetworkServerAloneAlsoNeedsAStackAndSaysWhatItCannotEnforce() {
        var unit = TestSandbox.Unit(BundlePermissions.NetworkServer);

        Assert.Equal(SandboxLayout.DefaultNetworkNamespace, unit.ValueOf("NetworkNamespacePath"));

        var gap = Assert.Single(unit.Gaps, g => g.Subject == BundlePermissions.NetworkServer);
        Assert.Equal(SandboxGapKind.NoSuchProperty, gap.Kind);
    }

    [Fact]
    public void NetworkClientAloneClaimsNoSeparationItDoesNotHave() {
        // The other half of the case above: an application with only
        // network.client is *not* prevented from listening, because nothing
        // available separates the two. What must not happen is the unit implying
        // otherwise by recording a gap that is not there.
        var unit = TestSandbox.Unit(BundlePermissions.NetworkClient);

        Assert.DoesNotContain(unit.Gaps, g => g.Kind == SandboxGapKind.NoSuchProperty);
    }

    // --- files ---------------------------------------------------------------

    [Fact]
    public void FilesHomeMountsNothing() {
        // Doc 04's sharpest rule, and the one a reasonable implementer gets wrong:
        // files.home is weak-by-default and strong-by-use. The grant is the user
        // picking a file in the shell's chooser and the broker passing back an fd,
        // so declaring the permission must not put the user's home anywhere near
        // the container.
        var granted = TestSandbox.Unit(BundlePermissions.FilesHome);
        var withheld = TestSandbox.Unit();

        // ⚠ Asserted over bind *sources* rather than over the whole property text,
        // because /home/jiu legitimately appears as a destination: the container's
        // $HOME is /home/<user>, which spells the same string as the user's real
        // home while being a different directory in a different mount namespace.
        // Searching for the string would either fail on that or be weakened until
        // it proved nothing. What must not exist is a mount whose *source* is the
        // user's home.
        Assert.DoesNotContain(TestSandbox.UserHome, granted.BindSources(), StringComparer.Ordinal);

        // And the only thing under it that is mounted at all is the container.
        Assert.Equal(
            [TestSandbox.Layout().ContainerData],
            granted.BindSources().Where(s => s.StartsWith(TestSandbox.UserHome + "/", StringComparison.Ordinal))
        );

        // Stronger, and the form that survives someone adding a property later:
        // the two units differ in nothing a mount namespace can see.
        Assert.Equal(
            withheld.Properties.Select(p => p.Name + "=" + p.Value),
            granted.Properties.Select(p => p.Name + "=" + p.Value)
        );
    }

    [Fact]
    public void FilesHomeIsRecordedAsSomethingTheBrokerOwes() {
        var unit = TestSandbox.Unit(BundlePermissions.FilesHome);

        var gap = Assert.Single(unit.Gaps, g => g.Subject == BundlePermissions.FilesHome);
        Assert.Equal(SandboxGapKind.Broker, gap.Kind);
    }

    [Fact]
    public void FilesRemovableMountsNothingEither() {
        var unit = TestSandbox.Unit(BundlePermissions.FilesRemovable);

        Assert.DoesNotContain(unit.AllValues(), value => value.Contains("/Volumes", StringComparison.Ordinal));
        Assert.Contains(unit.Gaps, g => g.Subject == BundlePermissions.FilesRemovable && g.Kind == SandboxGapKind.Broker);
    }

    [Fact]
    public void TheApplicationsHomeIsItsContainerAndNotTheUsers() {
        var unit = TestSandbox.Unit();
        var layout = TestSandbox.Layout();

        Assert.Contains(layout.ContainerData + ":/home/jiu", unit.ValuesOf("BindPaths"), StringComparer.Ordinal);
        Assert.Contains("HOME=/home/jiu", unit.Environment, StringComparer.Ordinal);

        // ⚠ The source of that bind is ~/Library/Containers/<id>/Data and the
        // destination is /home/<user>. Both spell /home/jiu — the destination
        // because the app's $HOME must be the home /etc/passwd names for it, the
        // source because the containers live in the user's real home. The thing
        // that must never appear is /home/jiu bound onto itself.
        Assert.DoesNotContain("/home/jiu:/home/jiu", unit.ValuesOf("BindPaths"), StringComparer.Ordinal);
        Assert.StartsWith(TestSandbox.UserHome + "/Library/Containers/", layout.ContainerData, StringComparison.Ordinal);
    }

    // --- display -------------------------------------------------------------

    [Fact]
    public void DisplayBindsTheCompositorSocketWritable() {
        var unit = TestSandbox.Unit(BundlePermissions.Display);
        var socket = TestSandbox.Layout().WaylandSocket;

        // ⚠ BindPaths, not BindReadOnlyPaths: connect() on a unix socket needs
        // write permission on the inode.
        Assert.Contains(socket + ":" + socket, unit.ValuesOf("BindPaths"), StringComparer.Ordinal);
        Assert.DoesNotContain(socket, unit.ValuesOf("BindReadOnlyPaths"), StringComparer.Ordinal);
        Assert.Contains("WAYLAND_DISPLAY=wayland-0", unit.Environment, StringComparer.Ordinal);
    }

    [Fact]
    public void WithoutDisplayThereIsNoSocketAndNoVariable() {
        var unit = TestSandbox.Unit();

        Assert.DoesNotContain(unit.AllValues(), value => value.Contains("wayland", StringComparison.Ordinal));
        Assert.DoesNotContain(unit.Environment, variable => variable.StartsWith("WAYLAND_", StringComparison.Ordinal));
    }

    [Fact]
    public void DisplayIsRecordedAsTheCompositorsToGrant() {
        // A bound socket is not a granted window, and the difference matters to
        // doc 04's consent dialog: the anchoring guarantee is the compositor's.
        var unit = TestSandbox.Unit(BundlePermissions.Display);

        Assert.Contains(unit.Gaps, g => g.Subject == BundlePermissions.Display && g.Kind == SandboxGapKind.Compositor);
    }

    // --- the unconditional floor --------------------------------------------

    [Theory]
    [InlineData("RootDirectory", SandboxLayout.DefaultComposedRoot)]
    [InlineData("PrivateDevices", "yes")]
    [InlineData("DevicePolicy", "closed")]
    [InlineData("ProtectProc", "invisible")]
    [InlineData("NoNewPrivileges", "yes")]
    [InlineData("SystemCallFilter", "@system-service")]
    [InlineData("SystemCallArchitectures", "native")]
    [InlineData("ProtectKernelTunables", "yes")]
    [InlineData("ProtectKernelModules", "yes")]
    [InlineData("ProtectControlGroups", "yes")]
    [InlineData("RestrictRealtime", "yes")]
    [InlineData("RestrictSUIDSGID", "yes")]
    [InlineData("PrivateTmp", "yes")]
    [InlineData("MountAPIVFS", "yes")]
    public void TheFloorAppliesToAnApplicationThatDeclaredNothing(string property, string value) {
        Assert.Equal(value, TestSandbox.Unit().ValueOf(property));
    }

    [Fact]
    public void DeclaringEverythingDoesNotRelaxAnythingButTheNetworkNamespace() {
        // The invariant the whole builder rests on: permissions add, they never
        // subtract. If a future edit makes some permission remove a restriction,
        // this is what notices — and the single documented exception is spelled
        // out rather than being a hole in the assertion.
        var bare = TestSandbox.Unit();
        var everything = TestSandbox.Unit([.. BundlePermissions.Known]);

        var relaxed = bare.Properties
            .Where(p => everything.ValuesOf(p.Name).All(v => v != p.Value))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["PrivateNetwork"], relaxed);
    }

    [Fact]
    public void PrivatePidsIsOffUnlessTheSystemSaysItIsAvailable() {
        Assert.False(TestSandbox.Unit().Sets("PrivatePIDs"));

        var options = new SandboxOptions {
            Capabilities = SandboxCapabilities.Designed with { PrivatePids = true }
        };

        Assert.Equal("yes", TestSandbox.Unit(options).ValueOf("PrivatePIDs"));
    }

    [Fact]
    public void ProcSubsetIsNeverSet() {
        // Deliberate, and the comment in SandboxUnitBuilder.Processes says why:
        // ProcSubset=pid hides /proc/meminfo, which the .NET GC sizes itself from.
        // The failure would be an application with wrong memory heuristics rather
        // than a refusal, which is the hardest kind of sandbox bug to trace.
        Assert.False(TestSandbox.Unit().Sets("ProcSubset"));
    }

    // --- resource bounds -----------------------------------------------------

    [Fact]
    public void TheResourceBoundsAreSetAndAreOverridable() {
        var unit = TestSandbox.Unit();
        Assert.Equal("75%", unit.ValueOf("MemoryMax"));
        Assert.Equal("100", unit.ValueOf("CPUWeight"));
        Assert.Equal("512", unit.ValueOf("TasksMax"));

        var tightened = TestSandbox.Unit(new SandboxOptions {
            Resources = new SandboxResourceBounds { MemoryMax = "512M", CpuWeight = 20, TasksMax = 64 }
        });

        Assert.Equal("512M", tightened.ValueOf("MemoryMax"));
        Assert.Equal("20", tightened.ValueOf("CPUWeight"));
        Assert.Equal("64", tightened.ValueOf("TasksMax"));
    }

    // --- refusals ------------------------------------------------------------

    [Fact]
    public void APermissionThisSystemDoesNotDefineIsARefusalToLaunch() {
        // The case this slice exists for. A bundle signed by a future Trinix
        // asking for something we do not understand must not be run with the
        // string quietly dropped: it would get less authority than its developer
        // designed for and than its consent screen described, and nobody would
        // learn why it misbehaved.
        var info = TestSandbox.Info(BundlePermissions.Display, "devices.neuralink");

        var refusal = Assert.Throws<BundleException>(
            () => SandboxUnitBuilder.Build(info, TestSandbox.Layout(info))
        );

        Assert.Equal(BundleFailure.UnknownPermission, refusal.Failure);
        Assert.Contains("devices.neuralink", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePathInTheLayoutIsARefusalRatherThanAMountOfSomethingElse() {
        // systemd resolves BindPaths= against the working directory, so a relative
        // path does not fail — it names something else, inside a namespace nobody
        // can inspect afterwards.
        var info = TestSandbox.Info();
        var layout = TestSandbox.Layout(info) with { ComposedRoot = "sandbox/root" };

        var refusal = Assert.Throws<BundleException>(() => SandboxUnitBuilder.Build(info, layout));
        Assert.Contains("not an absolute path", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryPointThatEscapesTheBundleIsARefusal() {
        // Checked again here even though BundleVerifier checked it: a verified
        // signature proves a developer signed the manifest, not that they were
        // careful, and this value becomes an absolute path in ExecStart=.
        var info = new BundleInfo {
            Identifier = TestSandbox.Identifier,
            Name = "Hello",
            Version = "1.0.0",
            EntryPoint = "Contents/../../bin/sh"
        };

        var refusal = Assert.Throws<BundleException>(
            () => SandboxUnitBuilder.Build(info, TestSandbox.Layout(info))
        );

        Assert.Equal(BundleFailure.MalformedInfo, refusal.Failure);
    }

    // --- naming and identity -------------------------------------------------

    [Fact]
    public void TheUnitIsNamedAfterTheApplicationSoTheBrokerCanAttributeIt() {
        // Doc 04: the broker decides who is asking from the caller's cgroup, which
        // maps to the transient unit, which maps to a verified bundle identity.
        Assert.Equal("trinix-app-io.trinix.hello.service", TestSandbox.Unit().UnitName);
        Assert.Equal(
            "trinix-app-io.trinix.hello-7f2.service",
            TestSandbox.Unit(new SandboxOptions { Instance = "7f2" }).UnitName
        );
    }

    [Fact]
    public void TheApplicationRunsAsTheLoggedInUserAndNotADynamicOne() {
        var unit = TestSandbox.Unit();

        Assert.Equal("jiu", unit.ValueOf("User"));
        Assert.Equal("jiu", unit.ValueOf("Group"));

        // A dynamic uid changes between launches, so everything written last time
        // would belong to a user that no longer exists.
        Assert.False(unit.Sets("DynamicUser"));
    }

    [Fact]
    public void TheBundleIsBoundInReadOnlyAtTheSamePath() {
        var unit = TestSandbox.Unit();

        Assert.Contains(TestSandbox.BundlePath, unit.ValuesOf("BindReadOnlyPaths"), StringComparer.Ordinal);
        Assert.DoesNotContain(TestSandbox.BundlePath, unit.ValuesOf("BindPaths"), StringComparer.Ordinal);

        // Identity-mapped, which is what lets TRINIX_BUNDLE keep its host value
        // and a journal line name the same string as `ls /Applications`.
        Assert.Equal("/Applications/Hello.app/Contents/Bin/hello", unit.Program);
        Assert.Contains("TRINIX_BUNDLE=/Applications/Hello.app", unit.Environment, StringComparer.Ordinal);
    }

    [Fact]
    public void ExactlyThreeOfTheFourteenChangeAUnitPropertyAtAll() {
        // ⚠ This number is quoted in docs/app-bundles.md § 1, which is why it is
        // asserted rather than counted by hand: a permission that quietly starts or
        // stops touching the unit would make the document wrong with nothing
        // failing. It is also the honest shape of doc 04 — the unit is a floor, and
        // authority above the floor belongs to a broker that does not exist yet.
        var floor = Rendered(TestSandbox.Unit());

        List<string> changed = [];
        foreach (var permission in BundlePermissions.Known) {
            if (!Rendered(TestSandbox.Unit(permission)).SequenceEqual(floor, StringComparer.Ordinal)) {
                changed.Add(permission);
            }
        }

        Assert.Equal(
            [BundlePermissions.Display, BundlePermissions.NetworkClient, BundlePermissions.NetworkServer],
            changed
        );

        static IReadOnlyList<string> Rendered(SandboxUnit unit) =>
            [.. unit.Properties.Select(property => property.Name + "=" + property.Value)];
    }

    [Fact]
    public void TheContainerHasNoPathBecauseItHasNoUsrBin() {
        // Setting a PATH that resolves to nothing would turn "this was never going
        // to work here" into a runtime mystery.
        Assert.DoesNotContain(TestSandbox.Unit().Environment, v => v.StartsWith("PATH=", StringComparison.Ordinal));
    }
}

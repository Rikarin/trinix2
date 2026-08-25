using Trinix.Bundle;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     What the unit does <i>not</i> enforce, and whether it admits it.
/// </summary>
/// <remarks>
///     <para>
///         Doc 04's migration plan opens with audit mode: "the units are constructed,
///         and every violation is logged and allowed". That is only worth running if
///         the thing doing the logging knows what was never enforced in the first
///         place — a permission with no property, on a systemd that could not have
///         applied it, is not a violation waiting to happen but a gap that was there
///         at launch.
///     </para>
///     <para>
///         So <see cref="SandboxUnit.Gaps" /> is a first-class output and these tests
///         treat it as one. The failure they are written against is the tempting one:
///         a builder that quietly does less than doc 04 says and produces a unit that
///         looks complete.
///     </para>
/// </remarks>
public class SandboxGapTests {
    /// <summary>What the builder is given when it is told this image's systemd.</summary>
    static SandboxOptions AsBuiltToday(BundleRuntime runtime = BundleRuntime.Unknown) =>
        new() { Capabilities = SandboxCapabilities.TrinixToday, Runtime = runtime };

    [Fact]
    public void TheTwoPropertiesThisSystemdRefusesAreNotEmittedAtAll() {
        // ⚠ Measured, not predicted. Over D-Bus, SystemCallFilter= and
        // SystemCallArchitectures= fail the method call on a systemd built without
        // libseccomp — "Cannot set property SystemCallFilter, or unknown property" —
        // so emitting them produces no application rather than a hardened one. The
        // unit must be constructible anyway and must say what it lost.
        var unit = TestSandbox.Unit(AsBuiltToday());

        foreach (var property in SandboxCapabilities.RefusedWithoutSeccomp) {
            Assert.False(unit.Sets(property), property + " must not be emitted: the call would fail");

            var gap = Assert.Single(unit.Gaps, g => g.Subject == property);
            Assert.Equal(SandboxGapKind.SystemCapability, gap.Kind);
        }
    }

    [Fact]
    public void TheThreePropertiesThisSystemdAcceptsAreEmittedAndCalledTheatre() {
        // ⚠ The heart of this file. These three are accepted over the bus, are read
        // back by `systemctl show` exactly as they would be on a machine that
        // enforces them, and enforce nothing — `chrt -r 1` succeeds and `chmod u+s`
        // sticks, inside a unit that sets both. That is strictly worse than the two
        // above: a refusal is loud and this is silent. Withholding them would buy
        // nothing, since the enforcement is missing from the binary either way, so
        // they are emitted and the silence is what gets fixed.
        var unit = TestSandbox.Unit(AsBuiltToday(BundleRuntime.Native));

        foreach (var property in SandboxCapabilities.InertWithoutSeccomp) {
            Assert.True(unit.Sets(property), property + " should still be emitted: systemd accepts it");
            Assert.False(unit.Enforces(property), property + " must not be claimed as enforcing");

            var gap = Assert.Single(unit.Gaps, g => g.Subject == property);
            Assert.Equal(SandboxGapKind.Inert, gap.Kind);
        }

        Assert.Equal(
            SandboxCapabilities.InertWithoutSeccomp.Order(StringComparer.Ordinal),
            unit.InertProperties.Select(p => p.Name).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void AnInertPropertyIsNeverMistakenForAnEnforcedOne() {
        // The distinction the type system is supposed to carry. `Sets` answers the
        // question systemctl answers; `Enforces` answers the question a security
        // claim actually means, and on this build they differ for exactly three
        // properties.
        var designed = TestSandbox.Unit(new SandboxOptions { Runtime = BundleRuntime.Native });
        var today = TestSandbox.Unit(AsBuiltToday(BundleRuntime.Native));

        var divergent = today.Properties
            .Where(p => today.Sets(p.Name) && !today.Enforces(p.Name))
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(SandboxCapabilities.InertWithoutSeccomp.Order(StringComparer.Ordinal), divergent);

        // And on a systemd that has libseccomp, the two questions agree again.
        Assert.All(designed.Properties, p => Assert.True(designed.Enforces(p.Name)));
        Assert.Empty(designed.InertProperties);
    }

    [Fact]
    public void MemoryDenyWriteExecuteIsOnlyTheatreWhenItClaimsSomething() {
        // ⚠ MemoryDenyWriteExecute=no restricts nothing by design — it is what a
        // correct system emits for a JIT. Recording it as an unenforced restriction
        // would put a line in the journal that is true of every machine, which is
        // how a log stops being read.
        var jit = TestSandbox.Unit(AsBuiltToday(BundleRuntime.Managed));

        Assert.Equal("no", jit.ValueOf("MemoryDenyWriteExecute"));
        Assert.DoesNotContain(
            jit.Gaps,
            gap => gap.Subject == "MemoryDenyWriteExecute" && gap.Kind == SandboxGapKind.Inert
        );

        // The Runtime gap is still there, and it is a different statement: W^X is
        // off because the JIT needs it, not because systemd cannot do it.
        var gap = Assert.Single(jit.Gaps, g => g.Subject == "MemoryDenyWriteExecute");
        Assert.Equal(SandboxGapKind.Runtime, gap.Kind);
    }

    [Fact]
    public void EveryInertGapQuotesTheMeasurementRatherThanAsserting() {
        // A journal line that says "this enforces nothing" and cannot say how anyone
        // knows is a line the next reader has to re-derive. Each of the three carries
        // the thing that was watched not happening.
        var unit = TestSandbox.Unit(AsBuiltToday(BundleRuntime.Native));
        var reasons = unit.Gaps.Where(g => g.Kind == SandboxGapKind.Inert).Select(g => g.Reason).ToList();

        Assert.Equal(3, reasons.Count);
        Assert.Contains(reasons, r => r.Contains("chrt -r 1", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("mode 4644", StringComparison.Ordinal));
        Assert.All(reasons, r => Assert.Contains("systemctl show", r, StringComparison.Ordinal));
    }

    [Fact]
    public void NoNewPrivilegesSurvivesASystemdWithoutSeccomp() {
        // The one member of the syscall-floor row that is a prctl rather than a
        // seccomp filter. Losing it along with its neighbours would be the easy
        // mistake, and it is the most valuable single property in the row.
        var unit = TestSandbox.Unit(AsBuiltToday());

        Assert.Equal("yes", unit.ValueOf("NoNewPrivileges"));
        Assert.True(unit.Enforces("NoNewPrivileges"));
    }

    [Fact]
    public void TheMountAndCapabilityHardeningSurvivesTooBecauseItIsNotSeccomp() {
        var unit = TestSandbox.Unit(AsBuiltToday());

        Assert.Equal("yes", unit.ValueOf("ProtectKernelTunables"));
        Assert.Equal("yes", unit.ValueOf("ProtectKernelModules"));
        Assert.Equal("yes", unit.ValueOf("ProtectControlGroups"));
        Assert.Equal(SandboxLayout.DefaultComposedRoot, unit.ValueOf("RootDirectory"));

        // ✅ And the one whose enforcement was actually watched: PrivateNetwork=yes
        // removes eth0 inside the unit and does not in the control, which is what
        // makes the same harness believable when it says RestrictRealtime= does
        // nothing.
        Assert.True(unit.Enforces("PrivateNetwork"));
    }

    [Fact]
    public void OnlyTheFiveSeccompPropertiesEverDependOnHowSystemdWasBuilt() {
        // ⚠ The guard against the quiet version of this bug: a property that grows a
        // seccomp dependency, is emitted through the wrong helper, and therefore
        // claims an enforcement nobody measured. Any property whose enforcement
        // differs between the two capability sets must be one of the five, and every
        // one of the five must differ.
        var designed = TestSandbox.Unit(new SandboxOptions { Runtime = BundleRuntime.Native });
        var today = TestSandbox.Unit(AsBuiltToday(BundleRuntime.Native));

        var differs = designed.Properties
            .Where(p => !today.Enforces(p.Name))
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(SandboxCapabilities.ImplementedWithSeccomp.Order(StringComparer.Ordinal), differs);
    }

    // --- MemoryDenyWriteExecute, the per-bundle exception --------------------

    [Fact]
    public void ANativeAotBundleGetsWriteXorExecuteAndNoGap() {
        var unit = TestSandbox.Unit(new SandboxOptions { Runtime = BundleRuntime.Native });

        Assert.Equal("yes", unit.ValueOf("MemoryDenyWriteExecute"));
        Assert.DoesNotContain(unit.Gaps, gap => gap.Subject == "MemoryDenyWriteExecute");
    }

    [Fact]
    public void ADotnetBundleDoesNotAndTheExceptionIsRecordedPerBundle() {
        // Doc 04: off for .NET applications because the JIT needs W^X transitions,
        // "recorded per-bundle rather than being a system-wide surrender". The
        // record is the gap.
        var unit = TestSandbox.Unit(new SandboxOptions { Runtime = BundleRuntime.Managed });

        Assert.Equal("no", unit.ValueOf("MemoryDenyWriteExecute"));
        var gap = Assert.Single(unit.Gaps, g => g.Subject == "MemoryDenyWriteExecute");
        Assert.Equal(SandboxGapKind.Runtime, gap.Kind);
    }

    [Fact]
    public void ABundleThatCannotSayWhatItIsGetsTheSafeAnswerAndADifferentGap() {
        // Unknown produces the same property as Managed and a different gap, and
        // the difference is the whole point: "this is a JIT and W^X is off for a
        // reason" is a decision, "we could not tell" is a request for a field in
        // a future Info.json.
        var unit = TestSandbox.Unit(new SandboxOptions { Runtime = BundleRuntime.Unknown });

        Assert.Equal("no", unit.ValueOf("MemoryDenyWriteExecute"));
        var gap = Assert.Single(unit.Gaps, g => g.Subject == "MemoryDenyWriteExecute");
        Assert.Equal(SandboxGapKind.BundleFormat, gap.Kind);
    }

    [Fact]
    public void UnknownIsTheDefaultBecauseNothingInTheFormatSaysOtherwise() {
        Assert.Equal(BundleRuntime.Unknown, new SandboxOptions().Runtime);
        Assert.Equal(BundleRuntime.Unknown, TestSandbox.Unit().Runtime);
    }

    // --- what the broker owes ------------------------------------------------

    [Fact]
    public void EveryPermissionWithNoPropertyIsRecordedAsSomeoneElsesJob() {
        // Ten of the fourteen have no systemd property, and reading that as a
        // shortfall would be reading doc 04 backwards: the unit's job is the
        // floor, and authority above the floor is the broker's. What must not
        // happen is a permission being declared, enforced by nothing, and
        // mentioned nowhere.
        var unit = TestSandbox.Unit([.. BundlePermissions.Known]);
        var accounted = unit.Gaps.Select(gap => gap.Subject).ToHashSet(StringComparer.Ordinal);

        foreach (var permission in BundlePermissions.Known) {
            var enforced = permission is BundlePermissions.NetworkClient;
            Assert.Equal(!enforced, accounted.Contains(permission));
        }
    }

    [Fact]
    public void TenOfTheFourteenAreTheBrokersAndTheBrokerDoesNotExist() {
        // ⚠ Quoted in docs/app-bundles.md § 1, and asserted here so the document
        // cannot drift away from the code without something failing. Ten of the
        // fourteen are enforced by a program that has not been written — the two
        // files.* and the eight the default arm covers — and that is doc 04's design
        // rather than a shortfall: the grant for those is the user picking a thing,
        // which is a conversation and not a mount.
        var unit = TestSandbox.Unit([.. BundlePermissions.Known]);

        var brokered = unit.Gaps.Where(gap => gap.Kind == SandboxGapKind.Broker).Select(gap => gap.Subject);

        Assert.Equal(10, brokered.Count());
        Assert.Contains(BundlePermissions.FilesHome, brokered, StringComparer.Ordinal);
        Assert.Contains(BundlePermissions.DevicesCamera, brokered, StringComparer.Ordinal);
    }

    [Fact]
    public void SystemBackgroundIsTheSessionManagersRatherThanTheBrokers() {
        // Whether a unit outlives its last window, and whether it is started at
        // login, is not an ExecContext property and never will be.
        var unit = TestSandbox.Unit(BundlePermissions.SystemBackground);

        var gap = Assert.Single(unit.Gaps, g => g.Subject == BundlePermissions.SystemBackground);
        Assert.Equal(SandboxGapKind.SessionManager, gap.Kind);
    }

    [Fact]
    public void AnApplicationThatAsksForNothingHasNoBrokerGaps() {
        var unit = TestSandbox.Unit();

        Assert.DoesNotContain(unit.Gaps, gap => gap.Kind == SandboxGapKind.Broker);
    }

    [Fact]
    public void EveryGapCarriesASentenceSomethingCanLog() {
        var unit = TestSandbox.Unit([.. BundlePermissions.Known]);

        Assert.NotEmpty(unit.Gaps);
        Assert.All(unit.Gaps, gap => {
            Assert.False(string.IsNullOrWhiteSpace(gap.Subject));
            Assert.False(string.IsNullOrWhiteSpace(gap.Reason));
        });
    }
}

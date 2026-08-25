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
    /// <summary>The five properties systemd implements with libseccomp and only then.</summary>
    static readonly string[] SeccompProperties = [
        "SystemCallFilter",
        "SystemCallArchitectures",
        "MemoryDenyWriteExecute",
        "RestrictRealtime",
        "RestrictSUIDSGID"
    ];

    [Fact]
    public void OnASystemdWithoutSeccompTheSyscallFloorIsOmittedAndSaidSo() {
        // ⚠ This is not hypothetical. base/recipes/systemd/recipe.sh builds
        // systemd 257 with -Dseccomp=disabled, under "security frameworks the base
        // image does not have" — so on Trinix as it is built today, doc 04's whole
        // syscall floor row is unavailable. The unit must be constructible anyway
        // and must say what it lost.
        var unit = TestSandbox.Unit(new SandboxOptions { Capabilities = SandboxCapabilities.TrinixToday });

        foreach (var property in SeccompProperties) {
            Assert.False(unit.Sets(property), property + " should not be emitted without libseccomp");
            Assert.Contains(
                unit.Gaps,
                gap => gap.Subject == property && gap.Kind == SandboxGapKind.SystemCapability
            );
        }
    }

    [Fact]
    public void NoNewPrivilegesSurvivesASystemdWithoutSeccomp() {
        // The one member of the syscall-floor row that is a prctl rather than a
        // seccomp filter. Losing it along with its neighbours would be the easy
        // mistake, and it is the most valuable single property in the row.
        var unit = TestSandbox.Unit(new SandboxOptions { Capabilities = SandboxCapabilities.TrinixToday });

        Assert.Equal("yes", unit.ValueOf("NoNewPrivileges"));
    }

    [Fact]
    public void TheMountAndCapabilityHardeningSurvivesTooBecauseItIsNotSeccomp() {
        var unit = TestSandbox.Unit(new SandboxOptions { Capabilities = SandboxCapabilities.TrinixToday });

        Assert.Equal("yes", unit.ValueOf("ProtectKernelTunables"));
        Assert.Equal("yes", unit.ValueOf("ProtectKernelModules"));
        Assert.Equal("yes", unit.ValueOf("ProtectControlGroups"));
        Assert.Equal(SandboxLayout.DefaultComposedRoot, unit.ValueOf("RootDirectory"));
    }

    [Fact]
    public void EverySeccompPropertyIsMarkedAsOneWhenItIsEmitted() {
        // The flag is what lets a caller strip them for a system that cannot take
        // them without the builder having to be reinvoked, and a property that
        // grew a seccomp dependency without the flag would silently break that.
        var unit = TestSandbox.Unit();
        var flagged = unit.Properties.Where(p => p.NeedsSeccomp).Select(p => p.Name).Order(StringComparer.Ordinal);

        Assert.Equal(SeccompProperties.Order(StringComparer.Ordinal), flagged);
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

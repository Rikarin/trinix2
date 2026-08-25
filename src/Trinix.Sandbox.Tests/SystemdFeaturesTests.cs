namespace Trinix.Sandbox.Tests;

/// <summary>
///     Reading a systemd's build configuration out of its own version banner.
/// </summary>
/// <remarks>
///     <para>
///         This replaces a constant that was derived by hand from reading
///         <c>base/recipes/systemd/recipe.sh</c> — a fact that is correct until somebody
///         changes the recipe and does not change the constant, which is the failure
///         mode where the launcher believes it has a syscall floor and does not.
///     </para>
///     <para>
///         ⚠ <b>The obvious probe was tried and it lies.</b> <c>systemctl show -p
///         SystemCallFilter</c> answers <c>SystemCallFilter=~</c> on the Trinix image —
///         a well-formed value, for the property whose transient setter fails the call —
///         because <c>show</c> only distinguishes property names systemd knows from ones
///         it does not. A probe built on it concludes all five seccomp properties are
///         available and is wrong about every one of them, in the direction that
///         produces a sandbox which looks armed. Hence the feature string, which knows
///         the actual cause.
///     </para>
///     <para>
///         ⚠ Every test here runs against a captured string. Nothing starts a process,
///         and <see cref="SystemdProbe" /> — the twenty lines that do — is unexercised
///         until this runs on a machine with a systemd on it.
///     </para>
/// </remarks>
public class SystemdFeaturesTests {
    /// <summary>
    ///     What the image's systemd reports.
    /// </summary>
    /// <remarks>
    ///     ⚠ The <i>signs</i> on these tokens are the ones read off the booted image on
    ///     2026-08-25; the list is not the whole banner, which on systemd 257 runs to
    ///     three dozen tokens. It deliberately is not, because the parser must not care
    ///     how many tokens there are, what order they come in, or which release added
    ///     them — a parser that did would break on a systemd upgrade, and it would break
    ///     by concluding the build has no seccomp, which is a silent downgrade rather
    ///     than an error.
    /// </remarks>
    const string TrinixImage = """
        systemd 257 (257.2-1)
        -PAM -AUDIT -SELINUX -ACL -OPENSSL -TPM2 -PCRE2 -BPF_FRAMEWORK -SECCOMP +KMOD default-hierarchy=unified
        """;

    /// <summary>A systemd built the way distributions build it.</summary>
    const string StockSystemd = """
        systemd 257 (257.2-1)
        +PAM +AUDIT +SELINUX +ACL +OPENSSL +TPM2 +PCRE2 +BPF_FRAMEWORK +SECCOMP +KMOD default-hierarchy=unified
        """;

    // --- what the image means ------------------------------------------------

    [Fact]
    public void TheImagesBannerMeansNoSyscallFloor() {
        var features = SystemdFeatures.Parse(TrinixImage);

        Assert.Equal(257, features.Version);
        Assert.False(features.Has(SystemdFeatures.Seccomp));
        Assert.False(SandboxCapabilities.From(features).Seccomp);
    }

    [Fact]
    public void TheProbeAgreesWithTheConstantItReplaces() {
        // ⚠ The point of keeping TrinixToday around at all. It is no longer an input
        // to a launch — the launcher probes — it is the expected answer, pinned here
        // so that a recipe change which flips -SECCOMP to +SECCOMP shows up as a
        // failing test rather than as a quietly different launch.
        Assert.Equal(
            SandboxCapabilities.TrinixToday.Seccomp,
            SandboxCapabilities.From(SystemdFeatures.Parse(TrinixImage)).Seccomp
        );
    }

    [Fact]
    public void TheImagesBannerSplitsTheFivePropertiesTheWayTheMeasurementDid() {
        // The whole reason the capability type has three states rather than two.
        var capabilities = SandboxCapabilities.From(SystemdFeatures.Parse(TrinixImage));

        Assert.Equal(PropertyEnforcement.Rejected, capabilities.EnforcementOf("SystemCallFilter"));
        Assert.Equal(PropertyEnforcement.Rejected, capabilities.EnforcementOf("SystemCallArchitectures"));
        Assert.Equal(PropertyEnforcement.Inert, capabilities.EnforcementOf("MemoryDenyWriteExecute"));
        Assert.Equal(PropertyEnforcement.Inert, capabilities.EnforcementOf("RestrictRealtime"));
        Assert.Equal(PropertyEnforcement.Inert, capabilities.EnforcementOf("RestrictSUIDSGID"));

        // And everything else is unaffected, including a property this type has never
        // heard of: it models one build-time decision, not a catalogue.
        Assert.Equal(PropertyEnforcement.Enforced, capabilities.EnforcementOf("PrivateNetwork"));
        Assert.Equal(PropertyEnforcement.Enforced, capabilities.EnforcementOf("SomeFutureProperty"));
    }

    [Fact]
    public void AStockSystemdGetsTheWholeFloorAndNoTheatre() {
        var capabilities = SandboxCapabilities.From(SystemdFeatures.Parse(StockSystemd));

        Assert.True(capabilities.Seccomp);
        Assert.All(
            SandboxCapabilities.ImplementedWithSeccomp,
            property => Assert.Equal(PropertyEnforcement.Enforced, capabilities.EnforcementOf(property))
        );
    }

    [Fact]
    public void TheSameStringAnswersForTheOtherDocumentsThatAssumedSystemdWouldHelp() {
        // ⚠ Doc 05 builds authentication on PAM and keychain sealing on TPM2, and doc
        // 10 re-seals a TPM policy across updates. systemd here can help with none of
        // it. That does not make those documents wrong — both are libraries Trinix can
        // link for itself — but nothing comes for free from systemd, and the feature
        // string is where that stops being a surprise.
        var features = SystemdFeatures.Parse(TrinixImage);

        Assert.False(features.Has("PAM"));
        Assert.False(features.Has("TPM2"));
        Assert.False(features.Has("AUDIT"));
        Assert.False(features.Has("BPF_FRAMEWORK"));
    }

    // --- the parser ----------------------------------------------------------

    [Fact]
    public void AVersionSuffixIsNotAFeatureNamedMinusOne() {
        // "(257.2-1)" contains a dash and is not a feature. The identifier rule is
        // what keeps it out, and getting this wrong would put junk in Absent where a
        // future feature lookup could collide with it.
        var features = SystemdFeatures.Parse(TrinixImage);

        Assert.All(features.Absent, name => Assert.False(name.Contains(')', StringComparison.Ordinal)));
        Assert.DoesNotContain("1", features.Absent);
        Assert.Equal(257, features.Version);
    }

    [Fact]
    public void NeitherOrderNorLineBreaksNorSpacingMatter() {
        var wrapped = "systemd 257 (257.2-1)\n+KMOD  -SECCOMP\n\n   -TPM2   -PAM\n";
        var features = SystemdFeatures.Parse(wrapped);

        Assert.False(features.Has(SystemdFeatures.Seccomp));
        Assert.True(features.Has("KMOD"));
        Assert.False(features.Has("TPM2"));
    }

    [Fact]
    public void AFeatureTheStringNeverMentionsIsUnknownRatherThanAbsent() {
        // ⚠ Three answers, and the third is not pedantry: a systemd older than a
        // feature prints nothing about it, exactly as a banner this parser failed to
        // read does. What silence means is a decision, and it belongs to the caller
        // rather than to a parser.
        var features = SystemdFeatures.Parse(TrinixImage);

        Assert.Null(features.Has("APPARMOR"));
    }

    [Fact]
    public void AnUnmentionedSeccompIsTakenAsAbsentBecauseThatIsTheSafeDirection() {
        // Assuming it is missing costs two properties that would have been enforced.
        // Assuming it is present costs a launch that fails with a D-Bus error and —
        // far worse — three properties recorded as enforcing while they are theatre.
        var silent = SystemdFeatures.Parse("systemd 257 (257.2-1)\n+KMOD default-hierarchy=unified");

        Assert.Null(silent.Has(SystemdFeatures.Seccomp));
        Assert.False(SandboxCapabilities.From(silent).Seccomp);
    }

    [Fact]
    public void CaseDoesNotMatterToACaller() {
        Assert.False(SystemdFeatures.Parse(TrinixImage).Has("seccomp"));
    }

    [Fact]
    public void SomethingThatIsNotABannerIsRefusedRatherThanGuessedAt() {
        Assert.False(SystemdFeatures.TryParse("command not found", out _, out var problem));
        Assert.Contains("not a systemd version banner", problem, StringComparison.Ordinal);

        Assert.False(SystemdFeatures.TryParse("", out _, out problem));
        Assert.False(SystemdFeatures.TryParse(null, out _, out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void AnUnreadableBannerIsStillDistinctFromAMeasuredAbsence() {
        // ⚠ SystemdFeatures.Unknown produces the same *properties* as a measured
        // -SECCOMP, because the safe direction is the same. What it must not lose is
        // the ability of a journal line to say "we could not read the version banner"
        // rather than claiming a measurement nobody made.
        Assert.Equal(string.Empty, SystemdFeatures.Unknown.VersionText);
        Assert.Null(SystemdFeatures.Unknown.Has(SystemdFeatures.Seccomp));
        Assert.False(SandboxCapabilities.From(SystemdFeatures.Unknown).Seccomp);

        Assert.NotEmpty(SandboxCapabilities.From(SystemdFeatures.Parse(TrinixImage)).Features!.VersionText);
    }

    [Fact]
    public void TheBannerIsCarriedSoARefusalCanQuoteItsEvidence() {
        // "The syscall floor is off" is an assertion. "The syscall floor is off
        // because systemctl --version says -SECCOMP" is one somebody can check.
        var capabilities = SandboxCapabilities.From(SystemdFeatures.Parse(TrinixImage));

        Assert.Equal("systemd 257 (257.2-1)", capabilities.Features!.VersionText);
    }
}

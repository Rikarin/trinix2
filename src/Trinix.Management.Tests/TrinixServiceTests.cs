namespace Trinix.Management.Tests;

/// <summary>
///     "Is this service doing what it is supposed to?" — the one judgement the
///     management module makes rather than reports.
/// </summary>
/// <remarks>
///     <para>
///         Everything else <c>Get-TrinixService</c> emits is systemd's own words
///         relayed. <see cref="TrinixService.IsHealthy" /> is different: it decides,
///         and it exists so that every caller does not have to rediscover that
///         <c>active/exited</c> is a one-shot unit that ran correctly rather than a
///         service that died.
///     </para>
///     <para>
///         ⚠ That makes it the property people will filter production alerts on, and
///         a rule that quietly widens is a rule that stops paging. The cases below are
///         the state combinations systemd actually produces.
///     </para>
/// </remarks>
public class TrinixServiceTests {
    static TrinixService Service(string state, string subState, string enabled = "enabled") => new() {
        Name = "trinixd.service",
        Description = "Trinix system service",
        State = state,
        SubState = subState,
        Enabled = enabled
    };

    [Theory]
    [InlineData("active", "running")] // the ordinary case
    [InlineData("active", "exited")] // a one-shot unit that ran and finished
    [InlineData("active", "waiting")]
    [InlineData("reloading", "reload")] // mid-reload is not broken
    public void AnActiveServiceIsHealthy(string state, string subState) {
        Assert.True(Service(state, subState).IsHealthy);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("disabled")]
    public void AUnitThatWasNeverMeantToStartIsHealthyWhileDead(string enabled) {
        // A static or disabled unit sitting inactive/dead is the system working
        // as configured — most of `systemctl list-units --all` looks like this.
        Assert.True(Service("inactive", "dead", enabled).IsHealthy);
    }

    [Fact]
    public void AnEnabledServiceThatIsNotRunningIsNotHealthy() {
        // ⚠ The case the rule exists for. "Enabled and dead" means something
        // that should have started did not, and it must not be filtered out
        // along with the hundreds of static units that are legitimately dead.
        Assert.False(Service("inactive", "dead").IsHealthy);
    }

    [Theory]
    [InlineData("failed", "failed")]
    [InlineData("failed", "dead")]
    public void AFailedServiceIsNeverHealthy(string state, string subState) {
        Assert.False(Service(state, subState).IsHealthy);
        Assert.False(Service(state, subState, "static").IsHealthy);
        Assert.False(Service(state, subState, "disabled").IsHealthy);
    }

    [Fact]
    public void ActivatingAndDeactivatingAreNotHealthy() {
        // Deliberately not "healthy yet". A unit stuck in start-pre is the
        // shape a hung dependency takes, and calling it healthy would hide it.
        Assert.False(Service("activating", "start-pre").IsHealthy);
        Assert.False(Service("deactivating", "stop").IsHealthy);
    }

    [Fact]
    public void AMaskedUnitThatIsInactiveIsNotHealthy() {
        // Masked is neither static nor disabled: it is an administrator having
        // forcibly wired the unit shut, which is worth surfacing rather than
        // folding into the "expected to be dead" bucket.
        Assert.False(Service("inactive", "dead", "masked").IsHealthy);
    }

    [Fact]
    public void TheComparisonsAreOrdinalNotCaseInsensitive() {
        // systemd's states are lowercase, always. Accepting "Active" would mean
        // accepting output this code never sees, and hiding a parse that went
        // wrong somewhere upstream.
        Assert.False(Service("Active", "running").IsHealthy);
        Assert.False(Service("ACTIVE", "running").IsHealthy);
    }
}

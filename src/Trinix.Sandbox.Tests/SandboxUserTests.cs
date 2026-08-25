using System.Globalization;
using Trinix.Bundle;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     Resolving the user a launch belongs to, against the machine the test is
///     running on.
/// </summary>
/// <remarks>
///     <para>
///         The only tests in this suite that touch the system, and they have to: the
///         thing being checked is precisely that <see cref="SandboxUser" /> asks
///         <c>getpwuid_r</c> instead of assuming. A fixture would assert the
///         assumption.
///     </para>
///     <para>
///         ⚠ <b>What runs here is Darwin's <c>struct passwd</c>, and Trinix runs
///         glibc's.</b> The two disagree — Darwin has <c>pw_change</c> and
///         <c>pw_class</c> between the gid and <c>pw_gecos</c> — so a developer's Mac
///         exercises one of the two declarations in
///         <see cref="SandboxUser" /> and Linux CI exercises the other. Neither run
///         covers both, and the failure a wrong layout produces is not a crash but a
///         home directory read out of the wrong field, which is why these assertions
///         check the *content* of what came back rather than only that something did.
///     </para>
/// </remarks>
public class SandboxUserTests {
    [Fact]
    public void TheCurrentUserComesFromThePasswdDatabaseAndAgreesWithTheRuntime() {
        Assert.True(SandboxUser.TryCurrent(out var user, out var problem), problem);

        // The cross-check that makes the struct layout assertable: .NET resolves the
        // same entry through its own native shim, so a name that agrees is a name
        // read out of the right field.
        Assert.Equal(Environment.UserName, user.Name);
        Assert.False(string.IsNullOrEmpty(user.GroupName));
        Assert.Null(problem);

        // ⚠ The assertion that catches a misdeclared struct. Every pointer in a
        // passwd entry is a plausible path, so "starts with a slash" would pass
        // just as happily on pw_shell — and reading the home out of the shell field
        // is precisely what the wrong platform's layout would do. A home is a
        // directory and a shell is not.
        Assert.True(Directory.Exists(user.Home), user.Home + " should be a directory");
    }

    [Fact]
    public void RootIsResolvableByUidBecauseTheLookupIsNotAboutThisProcess() {
        // uid 0 is the one entry both a Trinix image and a developer's Mac are
        // guaranteed to have, which makes it the only portable assertion about a
        // *specific* entry's contents.
        Assert.True(SandboxUser.TryResolve(0, out var root, out var problem), problem);

        Assert.Equal("root", root.Name);
        Assert.Equal(0u, root.UserId);
        Assert.StartsWith("/", root.Home, StringComparison.Ordinal);
    }

    [Fact]
    public void AUidWithNoEntryIsARefusalThatNamesIt() {
        // ⚠ Not an exception and not a guess. A launcher that invented a home for a
        // uid it could not resolve would put the container somewhere nothing else
        // will look for it, and the application would start and lose its files.
        const uint NoSuchUser = 0x7FFF_FFF0;

        Assert.False(SandboxUser.TryResolve(NoSuchUser, out var user, out var problem));
        Assert.Null(user);
        Assert.Contains(NoSuchUser.ToString(CultureInfo.InvariantCulture), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentThrowsRatherThanReturningSomethingPlausible() {
        // The throwing form is what SandboxLayout.ForCurrentUser uses, and on a
        // machine that can answer it must answer with the same thing TryCurrent did.
        Assert.True(SandboxUser.TryCurrent(out var expected, out _));

        Assert.Equal(expected, SandboxUser.Current());
    }

    // --- and what a layout built from one looks like -------------------------

    [Fact]
    public void ALayoutForTheCurrentUserPutsTheContainerInTheirRealHome() {
        var info = TestSandbox.Info(BundlePermissions.Display);
        var user = SandboxUser.Current();

        var layout = SandboxLayout.ForCurrentUser(info, TestSandbox.BundlePath);

        Assert.Equal(
            Path.Combine(user.Home, SandboxLayout.ContainersDirectory, info.Identifier),
            layout.ContainerRoot
        );

        Assert.Equal(
            Path.Combine(layout.ContainerRoot, SandboxLayout.ContainerDataDirectory),
            layout.ContainerData
        );

        // ⚠ The host's home is where the container is carved out of and is *not*
        // what the application sees: $HOME inside is /home/<name>, whatever the
        // passwd entry said outside. On this machine the two may happen to be the
        // same string — a Linux CI user really does live in /home/<name> — which is
        // exactly why the mount, asserted below, is the thing that matters and not
        // the spelling.
        Assert.Equal("/home/" + user.Name, layout.HomeInside);
    }

    [Fact]
    public void AResolvedUserIsAValidLayoutOnWhateverMachineThisIs() {
        var layout = SandboxLayout.ForCurrentUser(TestSandbox.Info(), TestSandbox.BundlePath);

        Assert.Empty(layout.Validate());

        var user = SandboxUser.Current();
        Assert.Equal("/run/user/" + user.UserId.ToString(CultureInfo.InvariantCulture), layout.RuntimeDirectory);
        Assert.StartsWith(layout.RuntimeDirectory + "/", layout.WaylandSocket, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnitBuiltFromAResolvedUserRunsAsThatUserAndNotADynamicOne() {
        var info = TestSandbox.Info();
        var user = SandboxUser.Current();
        var layout = SandboxLayout.For(info, TestSandbox.BundlePath, user);
        var unit = SandboxUnitBuilder.Build(info, layout);

        Assert.Equal(user.Name, unit.ValueOf("User"));
        Assert.Equal(user.GroupName, unit.ValueOf("Group"));
        Assert.False(unit.Sets("DynamicUser"));

        // ⚠ The real home is never a mount source. What is bound is the container
        // inside it, so resolving a true home through getpwuid_r buys the container
        // a correct location and buys the application no extra reach.
        Assert.DoesNotContain(user.Home, unit.BindSources());
        Assert.Contains(
            layout.ContainerData + ":" + layout.HomeInside,
            unit.ValuesOf("BindPaths"),
            StringComparer.Ordinal
        );
    }
}

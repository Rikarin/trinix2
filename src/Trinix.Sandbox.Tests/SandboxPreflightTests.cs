using Trinix.Bundle;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     Refusing by name, rather than emitting a unit that fails obscurely.
/// </summary>
/// <remarks>
///     <para>
///         Three of the things a sandboxed launch needs exist nowhere in this
///         repository: the composed root at <c>/usr/share/trinix/sandbox/root</c>, the
///         shared namespace at <c>/run/netns/trinix-apps</c>, and a privileged path to
///         the system manager. Doc 04 lists all three in its own "prerequisites this
///         document did not budget" table.
///     </para>
///     <para>
///         ⚠ The failure this suite exists to prevent is not "the launch fails" — it is
///         "the launch fails as a systemd error about a mount source, several layers
///         below the thing that is actually wrong, on a machine where the honest answer
///         is that nothing has ever created that directory".
///     </para>
/// </remarks>
public class SandboxPreflightTests {
    /// <summary>A machine where everything a sandbox needs happens to be present.</summary>
    static readonly Func<string, bool> Everything = _ => true;

    static PermissionSet Permissions(params string[] declared) => PermissionSet.Parse(declared);

    [Fact]
    public void AMachineThatHasEverythingHasNothingToSay() {
        Assert.Empty(SandboxPreflight.Check(TestSandbox.Layout(), Permissions(), Everything, privileged: true));
    }

    [Fact]
    public void TheComposedRootIsNamedAndIsNotThisProgramsToCreate() {
        // ⚠ The reason this is a refusal rather than a mkdir: the composed root is
        // what the container is *made of*. A launcher that created it would be
        // deciding, at runtime and as root, what an application's filesystem consists
        // of — the one decision doc 04 puts inside the image's integrity.
        var layout = TestSandbox.Layout();
        var missing = SandboxPreflight.Check(
            layout,
            Permissions(),
            path => path != layout.ComposedRoot,
            privileged: true
        );

        var prerequisite = Assert.Single(missing);
        Assert.Equal(SandboxLayout.DefaultComposedRoot, prerequisite.Subject);
        Assert.Equal(SandboxPrerequisiteOwner.SystemImage, prerequisite.Owner);
        Assert.Contains("base/", prerequisite.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedNamespaceIsOnlyRequiredByAnApplicationThatDeclaredNetwork() {
        // An application with no network permission never touches it, so its absence
        // must not stop that application launching — which is most of them, and is
        // the difference between a sandbox that can be turned on and one that cannot.
        var layout = TestSandbox.Layout();
        bool Exists(string path) => path != layout.NetworkNamespace;

        Assert.Empty(SandboxPreflight.Check(layout, Permissions(BundlePermissions.Display), Exists, privileged: true));

        var missing = SandboxPreflight.Check(
            layout,
            Permissions(BundlePermissions.NetworkClient),
            Exists,
            privileged: true
        );

        var prerequisite = Assert.Single(missing);
        Assert.Equal(SandboxLayout.DefaultNetworkNamespace, prerequisite.Subject);
        Assert.Equal(SandboxPrerequisiteOwner.Boot, prerequisite.Owner);
    }

    [Fact]
    public void ANetworkNamespaceIsAFileAndTheDefaultCheckKnowsThat() {
        // ⚠ The one place where Path.Exists and Directory.Exists give different
        // answers about a healthy machine. An entry in /run/netns is a regular file
        // that a namespace is bind-mounted onto, so a directory check would report the
        // shared namespace missing on a machine where it is present and working — and
        // would do it only for networked applications, only on a real system, which is
        // the hardest possible place to notice.
        var namespaceFile = Path.Combine(Path.GetTempPath(), "trinix-netns-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(namespaceFile, []);

        try {
            var layout = TestSandbox.Layout() with { NetworkNamespace = namespaceFile };

            // The real predicate, not an injected one: that is the whole point here.
            var missing = SandboxPreflight.Check(layout, Permissions(BundlePermissions.NetworkClient));

            Assert.DoesNotContain(missing, p => p.Subject == namespaceFile);
        } finally {
            File.Delete(namespaceFile);
        }
    }

    [Fact]
    public void AnUnprivilegedLaunchIsRefusedAndSaysNobodyOwnsTheFix() {
        // Doc 04, verbatim: a RootDirectory= unit needs the *system* manager, so the
        // launcher needs a privileged path to it that is currently unplanned. Falling
        // back to the user manager would take most of these properties and drop
        // RootDirectory=, producing an application with a sandbox-shaped unit and no
        // container.
        var missing = SandboxPreflight.Check(TestSandbox.Layout(), Permissions(), Everything, privileged: false);

        var prerequisite = Assert.Single(missing);
        Assert.Equal(SandboxPrerequisiteOwner.Unplanned, prerequisite.Owner);
        Assert.Contains("polkit", prerequisite.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoSystemdMachineSaysSoRatherThanFailingAtExec() {
        var missing = SandboxPreflight.Check(
            TestSandbox.Layout(),
            Permissions(),
            path => path != SystemdProbe.SystemdRun,
            privileged: true
        );

        var prerequisite = Assert.Single(missing);
        Assert.Equal(SystemdProbe.SystemdRun, prerequisite.Subject);
    }

    [Fact]
    public void EveryPrerequisiteNamesAPathOrACapabilityAndSaysWhoOwesIt() {
        var missing = SandboxPreflight.Check(
            TestSandbox.Layout(),
            Permissions(BundlePermissions.NetworkClient),
            _ => false,
            privileged: false
        );

        Assert.Equal(5, missing.Count);
        Assert.All(missing, p => {
            Assert.False(string.IsNullOrWhiteSpace(p.Subject));
            Assert.False(string.IsNullOrWhiteSpace(p.Problem));
        });
    }

    // --- the one thing the launcher may create -------------------------------

    [Fact]
    public void TheContainerIsCreatedWithEveryLevelAboveItAndIsPrivate() {
        // ⚠ Mode 0700. A container is one application's private state and the other
        // applications on the machine are exactly the parties it is private from.
        var home = Path.Combine(Path.GetTempPath(), "trinix-home-" + Guid.NewGuid().ToString("N"));
        var user = SandboxUser.Current();
        var info = TestSandbox.Info();
        var layout = SandboxLayout.For(info, TestSandbox.BundlePath, user.Name, user.UserId, home, user.GroupName);

        try {
            Assert.True(SandboxPreflight.TryPrepareContainer(layout, user.GroupId, out var problem), problem);
            Assert.True(Directory.Exists(layout.ContainerData));

            // Every level this call brought into existence, not only the leaf: a
            // root-owned ~/Library with a user-owned Data inside it is still a home
            // the application cannot reach.
            if (!OperatingSystem.IsWindows()) {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(layout.ContainerRoot)
                );
            }

            // And it is idempotent, because every launch calls it.
            Assert.True(SandboxPreflight.TryPrepareContainer(layout, user.GroupId, out problem), problem);
        } finally {
            if (Directory.Exists(home)) {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    [Fact]
    public void PreparingTheContainerIsWhatMakesTheCheckPass() {
        var home = Path.Combine(Path.GetTempPath(), "trinix-home-" + Guid.NewGuid().ToString("N"));
        var user = SandboxUser.Current();
        var info = TestSandbox.Info();
        var layout = SandboxLayout.For(info, TestSandbox.BundlePath, user.Name, user.UserId, home, user.GroupName);

        // ⚠ The container is asked about for real and everything else is waved
        // through, so the only thing this test can be measuring is the creation.
        IReadOnlyList<SandboxPrerequisite> Check() => SandboxPreflight.Check(
            layout,
            Permissions(),
            path => path == layout.ContainerData ? Path.Exists(path) : Everything(path),
            privileged: true
        );

        try {
            Assert.Contains(
                Check(),
                p => p.Subject == layout.ContainerData && p.Owner == SandboxPrerequisiteOwner.Launcher
            );

            Assert.True(SandboxPreflight.TryPrepareContainer(layout, user.GroupId, out _));

            Assert.Empty(Check());
        } finally {
            if (Directory.Exists(home)) {
                Directory.Delete(home, recursive: true);
            }
        }
    }
}

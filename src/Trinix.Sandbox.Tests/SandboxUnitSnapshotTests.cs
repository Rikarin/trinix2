using Trinix.Bundle;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     The whole unit, exactly as it would be sent, in one readable block.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the artefact the wiring slice owes, because the wiring itself
///         cannot be run.</b> Nothing in this repository can start a transient unit: the
///         development machine is a Mac, the composed root exists in no image, and the
///         privileged path to the system manager is unwritten. What can be done is to
///         make the thing that <i>would</i> be sent legible — so that the first person
///         with a booted Trinix can read this block, run
///         <c>open --print-unit Hello.app</c> next to it, and compare two texts rather
///         than debugging a launch.
///     </para>
///     <para>
///         The other suites assert one rule each, which is the right shape for a rule
///         and the wrong shape for "what does this actually produce". A property that
///         nobody thought to write a test for is invisible to all of them and visible
///         here. ⚠ The cost is the usual cost of a snapshot: it fails for cosmetic
///         changes as readily as for real ones. That is acceptable exactly once, for
///         one bundle, next to a dozen suites that assert meaning — and the fix when it
///         fails is to read the diff and decide, not to re-record it.
///     </para>
/// </remarks>
public class SandboxUnitSnapshotTests {
    /// <summary>
    ///     A windowed, networked .NET application on the image as it is built today.
    /// </summary>
    /// <remarks>
    ///     The most informative single case: <c>display</c> and <c>network.client</c>
    ///     are two of the three permissions that change a property at all, and
    ///     <see cref="BundleRuntime.Managed" /> is what every application Trinix has
    ///     today is.
    /// </remarks>
    [Fact]
    public void ThisIsWhatASandboxedLaunchWouldSendToday() {
        var info = TestSandbox.Info(BundlePermissions.Display, BundlePermissions.NetworkClient);

        var unit = SandboxUnitBuilder.Build(info, TestSandbox.Layout(info), new SandboxOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            Runtime = BundleRuntime.Managed
        });

        Assert.Equal(
            """
            [Unit]
            Description=Hello (io.trinix.hello)

            [Service]
            User=jiu
            Group=jiu
            Type=exec
            RootDirectory=/usr/share/trinix/sandbox/root
            MountAPIVFS=yes
            WorkingDirectory=/home/jiu
            TemporaryFileSystem=/run:mode=0755
            PrivateTmp=yes
            BindReadOnlyPaths=/usr/lib
            BindReadOnlyPaths=/usr/share/fonts
            BindReadOnlyPaths=/etc/dotnet
            BindReadOnlyPaths=/etc/ssl/certs
            BindReadOnlyPaths=/etc/os-release
            BindReadOnlyPaths=/Applications/Hello.app
            BindPaths=/home/jiu/Library/Containers/io.trinix.hello/Data:/home/jiu
            NetworkNamespacePath=/run/netns/trinix-apps
            PrivateDevices=yes
            DevicePolicy=closed
            ProtectProc=invisible
            NoNewPrivileges=yes
            # ⚠ accepted by this systemd and enforcing nothing — see Gaps
            MemoryDenyWriteExecute=no
            ProtectKernelTunables=yes
            ProtectKernelModules=yes
            ProtectControlGroups=yes
            # ⚠ accepted by this systemd and enforcing nothing — see Gaps
            RestrictRealtime=yes
            # ⚠ accepted by this systemd and enforcing nothing — see Gaps
            RestrictSUIDSGID=yes
            MemoryMax=75%
            CPUWeight=100
            TasksMax=512
            BindPaths=/run/user/1000/wayland-0:/run/user/1000/wayland-0
            Environment=HOME=/home/jiu
            Environment=USER=jiu
            Environment=LOGNAME=jiu
            Environment=XDG_RUNTIME_DIR=/run/user/1000
            Environment=TRINIX_BUNDLE=/Applications/Hello.app
            Environment=TRINIX_BUNDLE_IDENTIFIER=io.trinix.hello
            Environment=TRINIX_BUNDLE_RESOURCES=/Applications/Hello.app/Contents/Resources
            Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
            Environment=DOTNET_NOLOGO=1
            Environment=WAYLAND_DISPLAY=wayland-0
            ExecStart=/Applications/Hello.app/Contents/Bin/hello

            """,
            unit.ToUnitFile()
        );
    }

    /// <summary>
    ///     And this is what the launch would say about itself in the journal.
    /// </summary>
    /// <remarks>
    ///     Doc 04: "a permission enforced by nothing should be a line in the journal
    ///     rather than a silence." Six lines for an application that declares two
    ///     permissions — and the three that name a <i>property</i> rather than a
    ///     permission are the ones the document did not know it needed.
    /// </remarks>
    [Fact]
    public void AndThisIsWhatItWouldSayAboutWhatItIsNotDoing() {
        var info = TestSandbox.Info(BundlePermissions.Display, BundlePermissions.NetworkClient);

        var unit = SandboxUnitBuilder.Build(info, TestSandbox.Layout(info), new SandboxOptions {
            Capabilities = SandboxCapabilities.TrinixToday,
            Runtime = BundleRuntime.Managed
        });

        Assert.Equal(
            [
                "SystemCapability SystemCallFilter",
                "SystemCapability SystemCallArchitectures",
                "Runtime MemoryDenyWriteExecute",
                "Inert RestrictRealtime",
                "Inert RestrictSUIDSGID",
                "Compositor display"
            ],
            unit.Gaps.Select(gap => gap.Kind + " " + gap.Subject)
        );
    }
}

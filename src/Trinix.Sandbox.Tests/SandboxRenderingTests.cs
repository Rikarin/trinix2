using Trinix.Bundle;

namespace Trinix.Sandbox.Tests;

/// <summary>
///     Turning the unit into something that can be executed, and into something that
///     can be read.
/// </summary>
/// <remarks>
///     ⚠ Neither renderer is verified against a real <c>systemd-run</c>, and no test
///     in this file claims otherwise. What they pin is the shape — every property
///     reaches the argument list, arguments stay arguments rather than becoming a
///     shell string, and the application's own arguments cannot be mistaken for
///     <c>systemd-run</c>'s. Whether systemd 257 accepts the spellings is a question
///     for a booted machine.
/// </remarks>
public class SandboxRenderingTests {
    [Fact]
    public void EveryPropertyReachesTheArgumentList() {
        var unit = TestSandbox.Unit(BundlePermissions.Display, BundlePermissions.NetworkClient);
        var arguments = unit.ToSystemdRunArguments();

        Assert.All(
            unit.Properties,
            property => Assert.Contains($"--property={property.Name}={property.Value}", arguments, StringComparer.Ordinal)
        );

        Assert.All(
            unit.Environment,
            variable => Assert.Contains("--setenv=" + variable, arguments, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void TheApplicationComesAfterASeparatorSoItsArgumentsAreItsOwn() {
        var unit = TestSandbox.Unit(new SandboxOptions { Arguments = ["--frames", "10"] });
        var arguments = unit.ToSystemdRunArguments();

        var separator = arguments.ToList().IndexOf("--");
        Assert.True(separator > 0);
        Assert.Equal(
            [unit.Program, "--frames", "10"],
            arguments.Skip(separator + 1)
        );
    }

    [Fact]
    public void TheUnitAndItsDescriptionAreNamedOnTheCommandLine() {
        var unit = TestSandbox.Unit();
        var arguments = unit.ToSystemdRunArguments();

        Assert.Contains("--unit=trinix-app-io.trinix.hello.service", arguments, StringComparer.Ordinal);
        Assert.Contains("--description=Hello (io.trinix.hello)", arguments, StringComparer.Ordinal);

        // --collect, so a failed unit does not sit in a failed state blocking the
        // next launch under the same name; --quiet, because "Running as unit …"
        // on stderr is not for someone double-clicking an icon.
        Assert.Contains("--collect", arguments, StringComparer.Ordinal);
        Assert.Contains("--quiet", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void NothingIsQuotedBecauseNothingIsGoingThroughAShell() {
        // The list is handed to execve as argv, so a bundle whose name contains a
        // space is one argument either way. The moment a renderer returns a string
        // somebody pastes it into a shell and the escaping is wrong exactly once.
        var info = new BundleInfo {
            Identifier = "com.example.two-words",
            Name = "Two Words",
            Version = "1.0.0",
            EntryPoint = "Contents/Bin/two words"
        };

        var arguments = SandboxUnitBuilder
            .Build(info, TestSandbox.Layout(info))
            .ToSystemdRunArguments();

        Assert.Contains("--description=Two Words (com.example.two-words)", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, argument => argument.Contains('"', StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains('\\', StringComparison.Ordinal));
    }

    [Fact]
    public void TheUnitFileFormIsReadableAndCarriesTheSameFacts() {
        // For `trinix doctor` and for a bug report — "show me what this would run
        // as" is much easier to read as a unit file than as forty --property
        // arguments. ⚠ Nothing writes it anywhere: doc 04 refuses a unit file on
        // disk because it is a grant that outlives a re-signing.
        var text = TestSandbox.Unit(BundlePermissions.Display).ToUnitFile();

        Assert.StartsWith("[Unit]\nDescription=Hello (io.trinix.hello)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n[Service]\n", text, StringComparison.Ordinal);
        Assert.Contains("\nPrivateNetwork=yes\n", text, StringComparison.Ordinal);
        Assert.Contains("\nEnvironment=HOME=/home/jiu\n", text, StringComparison.Ordinal);
        Assert.Contains("\nExecStart=/Applications/Hello.app/Contents/Bin/hello\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnitFileFormMarksThePropertiesThatEnforceNothing() {
        // ⚠ The one respect in which this output is not what a unit file looks like,
        // and it is deliberate. Somebody reads this to find out what an application is
        // contained by; three lines that answer that question wrongly are worse than
        // no output at all. It is also why it must never be written to disk and loaded
        // back — which doc 04 forbids for an unrelated and stronger reason anyway.
        var unit = TestSandbox.Unit(new SandboxOptions {
            Capabilities = SandboxCapabilities.TrinixToday, Runtime = BundleRuntime.Native
        });

        var text = unit.ToUnitFile();

        Assert.Contains("# ⚠ accepted by this systemd and enforcing nothing", text, StringComparison.Ordinal);
        Assert.Contains(
            "# ⚠ accepted by this systemd and enforcing nothing — see Gaps\nRestrictRealtime=yes\n",
            text,
            StringComparison.Ordinal
        );

        // The refused two are absent entirely rather than commented out: they are not
        // in the unit at all, because setting them would fail the launch.
        Assert.DoesNotContain("SystemCallFilter", text, StringComparison.Ordinal);

        // And a property that does its job is rendered plainly.
        Assert.Contains("\nPrivateNetwork=yes\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedPropertiesAreKeptSeparateRatherThanJoined() {
        // BindReadOnlyPaths is emitted once per path. A single comma-joined value
        // would make one unreadable line out of five readable ones and would hide
        // which path a systemd error was about.
        var unit = TestSandbox.Unit();

        Assert.True(unit.ValuesOf("BindReadOnlyPaths").Count > 1);
        Assert.All(
            unit.ValuesOf("BindReadOnlyPaths"),
            value => Assert.DoesNotContain(',', value)
        );
    }
}

/// <summary>
///     Working out whether a bundle's entry point is a JIT, from the only evidence
///     the format currently carries.
/// </summary>
public class BundleRuntimeDetectionTests {
    [Fact]
    public void ARuntimeConfigMeansAJit() {
        Assert.Equal(
            BundleRuntime.Managed,
            BundleRuntimeDetection.DetectFrom([
                "Contents/Info.json",
                "Contents/Bin/hello",
                "Contents/Bin/hello.dll",
                "Contents/Bin/hello.runtimeconfig.json"
            ])
        );
    }

    [Fact]
    public void NoRuntimeConfigMeansUnknownAndNeverNative() {
        // ⚠ The asymmetry is the design. A runtime config is positive evidence of
        // a JIT and its absence is evidence of nothing — a bundle without one
        // might be NativeAOT, or Rust, or a shell script, or a browser with a JIT
        // of its own. Concluding Native from an absence would turn a wrong guess
        // into a segfault at the first compiled method, which is the worst shape a
        // sandbox bug can have: late, arbitrary, and nothing like a permission
        // error.
        Assert.Equal(
            BundleRuntime.Unknown,
            BundleRuntimeDetection.DetectFrom(["Contents/Info.json", "Contents/Bin/compositor"])
        );

        Assert.Equal(BundleRuntime.Unknown, BundleRuntimeDetection.DetectFrom([]));
    }

    [Fact]
    public void ItReadsTheSignedManifestRatherThanADirectory() {
        // The manifest is what the developer signed, so a conclusion drawn from it
        // is as trustworthy as the signature. A directory listing is whatever is
        // on disk at the moment somebody looked.
        var manifest = new BundleManifest {
            Identifier = TestSandbox.Identifier,
            Version = "1.0.0",
            Architecture = "arm64",
            SignedAt = DateTimeOffset.UnixEpoch,
            MerkleRoot = new string('0', 64),
            Entries = [
                new ManifestEntry { Path = "Contents/Bin/hello", Size = 1, Sha256 = new string('0', 64), Executable = true },
                new ManifestEntry {
                    Path = "Contents/Bin/hello.runtimeconfig.json", Size = 1, Sha256 = new string('0', 64), Executable = false
                }
            ]
        };

        Assert.Equal(BundleRuntime.Managed, BundleRuntimeDetection.DetectFrom(manifest));
    }
}

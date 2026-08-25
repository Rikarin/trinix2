namespace Trinix.Management.Tests;

/// <summary>
///     The parsers that stand between systemd's output and a PowerShell object.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Cli" />'s own remarks explain why this module shells out to
///         <c>systemctl</c> and <c>ip</c> rather than speaking D-Bus and netlink: both
///         protocols deserve real bindings in <c>Trinix.Interop</c>, and writing half of
///         them now would mean writing them twice. What makes the interim tolerable is
///         that neither tool's <i>machine-readable</i> output is parsed by regex.
///     </para>
///     <para>
///         ⚠ These fixtures are real <c>systemctl show</c> output, kept verbatim
///         including the parts that look like mistakes — the empty values, the repeated
///         key, the property whose value contains an <c>=</c>. Tidying them up would
///         remove exactly the cases worth having.
///     </para>
/// </remarks>
public class CliParsingTests {
    [Fact]
    public void ParsesKeyValueOutput() {
        var properties = Cli.ParseProperties(
            """
            Id=trinixd.service
            Description=Trinix system service
            ActiveState=active
            SubState=running
            MainPID=412
            """
        );

        Assert.Equal("trinixd.service", properties["Id"]);
        Assert.Equal("Trinix system service", properties["Description"]);
        Assert.Equal("412", properties["MainPID"]);
        Assert.Equal(5, properties.Count);
    }

    [Fact]
    public void KeepsEverythingAfterTheFirstEquals() {
        // ⚠ systemd's ExecStart and Environment values routinely contain "=".
        // Splitting on every separator instead of the first would truncate them
        // silently, and the cmdlet would report a command line that is not the
        // one the unit runs.
        var properties = Cli.ParseProperties("Environment=TRINIX_MODE=debug LANG=C.UTF-8\n");

        Assert.Equal("TRINIX_MODE=debug LANG=C.UTF-8", properties["Environment"]);
    }

    [Fact]
    public void AnEmptyValueIsAValue() {
        // `systemctl show` prints Key= for a property that is unset, and the
        // difference between "unset" and "absent" is what a caller checks.
        var properties = Cli.ParseProperties("MainPID=\nId=trinixd.service\n");

        Assert.True(properties.ContainsKey("MainPID"));
        Assert.Equal(string.Empty, properties["MainPID"]);
    }

    [Fact]
    public void TheLastValueWinsForARepeatedKey() {
        // Matching systemctl's own behaviour for properties it repeats.
        var properties = Cli.ParseProperties("After=basic.target\nAfter=network.target\n");

        Assert.Equal("network.target", properties["After"]);
    }

    [Fact]
    public void SkipsLinesThatAreNotKeyValue() {
        var properties = Cli.ParseProperties(
            """
            Id=trinixd.service

            a line with no separator
            =a value with no key
            SubState=running
            """
        );

        Assert.Equal(2, properties.Count);
        Assert.False(properties.ContainsKey(string.Empty));
    }

    [Fact]
    public void StripsTheCarriageReturnFromCrlfOutput() {
        // Nothing in the image emits CRLF, but a developer piping a captured
        // file through on a Windows machine will, and a trailing \r turns
        // "active" into something no comparison matches.
        var properties = Cli.ParseProperties("ActiveState=active\r\nSubState=running\r\n");

        Assert.Equal("active", properties["ActiveState"]);
        Assert.Equal("running", properties["SubState"]);
    }

    [Fact]
    public void KeysAreComparedOrdinally() {
        // ⚠ Not case-insensitively. systemd's property names are exact, and a
        // culture-aware or case-folding dictionary would make "Id" and "ID"
        // the same key on some machines and not others.
        var properties = Cli.ParseProperties("Id=one\nID=two\n");

        Assert.Equal(2, properties.Count);
        Assert.Equal("one", properties["Id"]);
        Assert.Equal("two", properties["ID"]);
    }

    [Fact]
    public void ParsingNothingGivesNothing() {
        Assert.Empty(Cli.ParseProperties(string.Empty));
        Assert.Empty(Cli.ParseProperties("\n\n\n"));
    }

    [Fact]
    public void LinesDropsBlanksAndTrimsWhatIsLeft() {
        // `systemctl list-units --plain --no-legend` still leaves leading
        // spaces on some versions, and a unit name with a space in front of it
        // is a unit `systemctl show` reports as not-found.
        var lines = Cli.Lines("  trinixd.service loaded active running  \n\n  sshd.service loaded active running\n");

        Assert.Equal(
            ["trinixd.service loaded active running", "sshd.service loaded active running"],
            lines
        );
    }

    [Fact]
    public void LinesHandlesBothLineEndings() {
        Assert.Equal(["one", "two", "three"], Cli.Lines("one\r\ntwo\rthree"));
    }

    [Fact]
    public void ReadingAFileThatIsNotThereGivesTheFallback() {
        // ⚠ Silent fallback is right here and would be wrong almost anywhere
        // else: these are diagnostic reads — the kernel version, the machine
        // ID — and a cmdlet that threw because one of them was missing would
        // report nothing at all about a system it could mostly describe.
        var missing = Path.Combine(Path.GetTempPath(), "trinix-tests-no-such-file");

        Assert.Equal("unknown", Cli.ReadFileOrDefault(missing, "unknown"));
    }

    [Fact]
    public void ReadingAFileTrimsIt() {
        // /proc and /sys files end in a newline that nothing wants to display.
        var path = Path.Combine(Path.GetTempPath(), $"trinix-tests-{Guid.NewGuid():n}");
        File.WriteAllText(path, "6.12.0-trinix\n");

        try {
            Assert.Equal("6.12.0-trinix", Cli.ReadFileOrDefault(path, "unknown"));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadingADirectoryGivesTheFallbackRatherThanThrowing() {
        Assert.Equal("unknown", Cli.ReadFileOrDefault(Path.GetTempPath(), "unknown"));
    }
}

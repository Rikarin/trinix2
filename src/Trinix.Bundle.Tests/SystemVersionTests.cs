namespace Trinix.Bundle.Tests;

/// <summary>
///     <c>minimumSystemVersion</c>: whether an application is allowed to run on the
///     system it finds itself on.
/// </summary>
/// <remarks>
///     <para>
///         This is the one comparison in the bundle format that fails <i>open</i>, and
///         the tests are arranged around that. <see cref="SystemVersion.Satisfies" />
///         answers "yes" whenever it does not know — no <c>os-release</c>, no
///         <c>minimumSystemVersion</c>, an unparseable version on either side — because
///         a launcher that refused to start anything on a machine missing
///         <c>/usr/lib/os-release</c> would be enforcing a version policy by accident,
///         and the failure would look nothing like its cause.
///     </para>
///     <para>
///         Fail-open is a deliberate choice, so it is worth having tests that say so
///         out loud. If someone later decides the default should be "no", these are the
///         cases they have to come and edit, which is exactly the amount of friction
///         that decision deserves.
///     </para>
/// </remarks>
public class SystemVersionTests {
    [Theory]
    [InlineData("0.3", "0.3")] // equal is enough: "at least"
    [InlineData("0.4", "0.3")]
    [InlineData("1.0", "0.9")]
    [InlineData("0.3.1", "0.3")]
    [InlineData("0.10", "0.9")] // numeric, not lexicographic: 10 > 9
    [InlineData("2.0.0", "1.99.99")]
    public void ANewerOrEqualSystemSatisfies(string system, string required) {
        Assert.True(SystemVersion.Satisfies(system, required));
    }

    [Theory]
    [InlineData("0.3", "0.4")]
    [InlineData("0.9", "0.10")]
    [InlineData("0.3", "0.3.1")] // a missing component is zero, and 0 < 1
    [InlineData("1.99.99", "2.0.0")]
    public void AnOlderSystemDoesNot(string system, string required) {
        Assert.False(SystemVersion.Satisfies(system, required));
    }

    [Theory]
    [InlineData("0.3", "0.3.0")]
    [InlineData("0.3.0", "0.3")]
    [InlineData("0.3.0.0", "0.3")]
    [InlineData("1", "1.0.0")]
    public void AMissingComponentIsZero(string system, string required) {
        // 0.3 and 0.3.0 are the same version in both directions, which is the
        // property that stops a trailing ".0" in os-release from changing an
        // application's compatibility.
        Assert.True(SystemVersion.Satisfies(system, required));
    }

    [Theory]
    [InlineData(null, "0.3")] // no os-release: a build machine, or a container
    [InlineData("", "0.3")]
    [InlineData("0.3", null)] // the application did not ask for anything
    [InlineData("0.3", "")]
    [InlineData(null, null)]
    public void NotKnowingMeansYes(string? system, string? required) {
        Assert.True(SystemVersion.Satisfies(system, required));
    }

    [Theory]
    [InlineData("unstable", "0.3")] // nothing numeric to compare
    [InlineData("0.3", "unstable")]
    [InlineData("v0.3", "0.3")] // the "v" is on the first component, so nothing parses
    public void AnUnparseableVersionOnEitherSideMeansYes(string system, string required) {
        Assert.True(SystemVersion.Satisfies(system, required));
    }

    [Fact]
    public void ASuffixTruncatesTheComponentItIsAttachedTo() {
        // ⚠ Records what the code does, which is not quite what Parse's comment
        // claims. "0.3-rc1" splits into ["0", "3-rc1"]; "3-rc1" does not parse,
        // so the comparison ends there and the version is read as "0" — not as
        // "0.3". A release candidate of 0.3 therefore does *not* satisfy a
        // minimumSystemVersion of 0.3, while 0.3.1-rc1 does, because by then two
        // components have already been compared and matched.
        //
        // That asymmetry is worth knowing about before Trinix ships a version
        // with a suffix in VERSION_ID. It is pinned rather than fixed here
        // because changing it changes which applications launch, and that is a
        // decision for whoever owns the versioning scheme rather than for a
        // test that happened to notice.
        Assert.False(SystemVersion.Satisfies("0.3-rc1", "0.3"));
        Assert.True(SystemVersion.Satisfies("0.3.1-rc1", "0.3"));

        // The sharp end of it: 0.4 is newer than 0.3 by any reading, and
        // "0.4-rc1" still fails, because only the leading "0" survived parsing.
        Assert.False(SystemVersion.Satisfies("0.4-rc1", "0.3"));
    }

    [Fact]
    public void NegativeAndSignedComponentsDoNotParse() {
        // NumberStyles.None: no sign, no thousands separator, no whitespace.
        // "-1" would otherwise compare as less than everything and "+1" as
        // equal to 1, and neither is a version anyone wrote on purpose.
        Assert.True(SystemVersion.Satisfies("-1.0", "0.3"));
        Assert.True(SystemVersion.Satisfies("0.3", "+1.0"));
    }

    [Fact]
    public void CurrentReadsVersionIdOutOfOsRelease() {
        var path = TempFile.WithContent(
            """
            NAME="Trinix"
            ID=trinix
            VERSION_ID="0.3"
            PRETTY_NAME="Trinix 0.3"
            """
        );

        Assert.Equal("0.3", SystemVersion.Current(path));
    }

    [Fact]
    public void CurrentStripsQuotesAndSurroundingSpace() {
        Assert.Equal("0.3", SystemVersion.Current(TempFile.WithContent("VERSION_ID=  \"0.3\"  ")));
        Assert.Equal("0.3", SystemVersion.Current(TempFile.WithContent("VERSION_ID=0.3")));
    }

    [Fact]
    public void CurrentTakesTheFirstVersionIdAndNotAKeyThatMerelyEndsInIt() {
        // ⚠ The match is a prefix on "VERSION_ID=", so a file listing
        // BUILD_VERSION_ID first must not shadow the real one.
        var path = TempFile.WithContent(
            """
            BUILD_VERSION_ID=99
            VERSION_ID=0.3
            """
        );

        Assert.Equal("0.3", SystemVersion.Current(path));
    }

    [Fact]
    public void CurrentIsNullWhenThereIsNothingToRead() {
        // The normal case on a build machine, and the reason Satisfies fails
        // open rather than closed.
        Assert.Null(SystemVersion.Current(Path.Combine(Path.GetTempPath(), "trinix-no-such-os-release")));
        Assert.Null(SystemVersion.Current(TempFile.WithContent("NAME=\"Trinix\"\nID=trinix\n")));
    }

    [Fact]
    public void OsReleasePathIsWhereTheImagePutsIt() {
        // /usr/lib rather than /etc: the image's copy is the one inside the
        // signed system, and /etc/os-release is a symlink to it that a
        // sufficiently determined administrator can replace.
        Assert.Equal("/usr/lib/os-release", SystemVersion.OsReleasePath);
    }
}

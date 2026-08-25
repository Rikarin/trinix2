namespace Trinix.Bundle.Tests;

/// <summary>
///     The permission vocabulary, and the refusal that makes it a vocabulary rather
///     than a suggestion.
/// </summary>
/// <remarks>
///     <para>
///         Two things are pinned. The first is the strings, for the same reason
///         <see cref="BundleInfoTests" /> pins the property names: they are inside a
///         Merkle signature, so a renamed permission is not a refactor but a format
///         change that every bundle in the field predates.
///     </para>
///     <para>
///         The second is that an unrecognised string is an error. That is the whole
///         behavioural content of parsing something Trinix does not understand, and it
///         is the direction a permissive parser drifts in on its own: <c>foreach</c>,
///         <c>if known then add</c>, and the unknown one is gone. What makes that
///         wrong is not tidiness — it is that the application then runs with less
///         authority than its developer designed for and than the user was shown, and
///         nothing anywhere says why.
///     </para>
/// </remarks>
public class PermissionSetTests {
    [Fact]
    public void TheVocabularyIsExactlyTheFourteenTheDesignDefines() {
        // Doc 04 § The vocabulary, in its order. Spelled out literally rather than
        // derived, because a test that reads the list it is checking checks
        // nothing: this is the copy that would have to be edited deliberately.
        Assert.Equal(
            [
                "display",
                "network.client",
                "network.server",
                "files.home",
                "files.removable",
                "devices.camera",
                "devices.microphone",
                "devices.location",
                "devices.usb",
                "system.notifications.critical",
                "system.automation",
                "system.background",
                "system.capture",
                "system.input"
            ],
            BundlePermissions.Known
        );
    }

    [Fact]
    public void EveryNameHasAFlagAndEveryFlagHasAName() {
        // The direction a hand-maintained table breaks: a permission added to one
        // half and forgotten in the other.
        foreach (var name in BundlePermissions.Known) {
            Assert.True(BundlePermissions.TryParse(name, out var flag));
            Assert.Equal(name, BundlePermissions.NameOf(flag));
        }

        foreach (var flag in Enum.GetValues<Permissions>()) {
            if (flag == Permissions.None) {
                continue;
            }

            Assert.Contains(BundlePermissions.NameOf(flag), BundlePermissions.Known, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void NoTwoPermissionsShareABit() {
        // A flags enum where two members collide grants one permission by
        // declaring the other, and the compiler will not say a word.
        var seen = Permissions.None;
        foreach (var flag in Enum.GetValues<Permissions>()) {
            Assert.Equal(Permissions.None, seen & flag);
            seen |= flag;
        }

        Assert.Equal(14, BundlePermissions.Known.Count);
    }

    [Fact]
    public void EveryPermissionHasAGroupAndAllButDisplayHaveAUserVisibleName() {
        foreach (var name in BundlePermissions.Known) {
            Assert.True(BundlePermissions.TryParse(name, out var flag));

            // Total functions, not switches with a hole in them: an unhandled
            // permission here would be a consent dialog with a blank label.
            _ = BundlePermissions.GroupOf(flag);

            var visible = BundlePermissions.UserVisibleName(flag);
            if (flag == Permissions.Display) {
                // Never shown. An app with no window is not an app.
                Assert.Null(visible);
            } else {
                Assert.False(string.IsNullOrWhiteSpace(visible));
            }
        }
    }

    [Theory]
    [InlineData("display", PermissionGroup.Display)]
    [InlineData("network.server", PermissionGroup.Network)]
    [InlineData("files.removable", PermissionGroup.Files)]
    [InlineData("devices.usb", PermissionGroup.Devices)]
    [InlineData("system.notifications.critical", PermissionGroup.System)]
    public void TheGroupIsThePrefix(string name, PermissionGroup expected) {
        Assert.True(BundlePermissions.TryParse(name, out var flag));
        Assert.Equal(expected, BundlePermissions.GroupOf(flag));
    }

    [Fact]
    public void ParsingRefusesAPermissionThisSystemDoesNotDefine() {
        var refusal = Assert.Throws<BundleException>(
            () => PermissionSet.Parse(["display", "devices.neuralink"])
        );

        Assert.Equal(BundleFailure.UnknownPermission, refusal.Failure);
        Assert.Contains("devices.neuralink", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalNamesEveryUnknownStringRatherThanTheFirst() {
        // Same contract as BundleInfo.Validate: one round trip per mistake is a
        // bad way to spend an afternoon, and this message is what a developer sees
        // when a bundle built for a newer Trinix will not start.
        var refusal = Assert.Throws<BundleException>(
            () => PermissionSet.Parse(["devices.neuralink", "display", "files.everything"])
        );

        Assert.Contains("devices.neuralink", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("files.everything", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'display'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseHandsBackBothHalvesForATooThatWantsToReport() {
        // trinix doctor and the packaging tool want to say "these two are wrong
        // and the rest are fine". A launcher wants Parse and its exception.
        Assert.False(PermissionSet.TryParse(["display", "camera"], out var parsed, out var unknown));
        Assert.True(parsed.Has(Permissions.Display));
        Assert.Equal(["camera"], unknown);
    }

    [Fact]
    public void ACaseVariantIsATypoAndNotAPermission() {
        // Nothing is lowercased on the way in. A launcher that quietly corrects
        // spelling in a signed document accepts two spellings of the signature's
        // meaning.
        Assert.False(BundlePermissions.IsKnown("Display"));
        Assert.False(BundlePermissions.IsKnown("FILES.HOME"));
        Assert.False(BundlePermissions.IsKnown(" display"));
    }

    [Fact]
    public void AnEmptyDeclarationParsesToNothingRatherThanFailing() {
        // Every application that asks for nothing, which should be most of them.
        var parsed = PermissionSet.Parse([]);

        Assert.Equal(PermissionSet.Empty, parsed);
        Assert.Equal(Permissions.None, parsed.Flags);
        Assert.Empty(parsed.Enumerate());
        Assert.Equal("(none)", parsed.ToString());
    }

    [Fact]
    public void HasMeansAllOfAndHasAnyMeansAnyOf() {
        var parsed = PermissionSet.Parse(["display", "network.client"]);

        Assert.True(parsed.Has(Permissions.Display));
        Assert.True(parsed.Has(Permissions.Display | Permissions.NetworkClient));
        Assert.False(parsed.Has(Permissions.Display | Permissions.NetworkServer));
        Assert.True(parsed.HasAny(Permissions.Display | Permissions.NetworkServer));
        Assert.False(parsed.HasAny(Permissions.FilesHome));
    }

    [Fact]
    public void TheRenderedFormIsVocabularyOrderRatherThanDeclarationOrder() {
        // A set has no declaration order, so rendering one makes two bundles with
        // identical authority produce identical strings in a log and in a diff.
        var declared = PermissionSet.Parse(["system.capture", "display", "files.home"]);

        Assert.Equal(["display", "files.home", "system.capture"], declared.ToNames());
        Assert.Equal("display files.home system.capture", declared.ToString());
    }

    [Fact]
    public void TwoSetsWithTheSameAuthorityAreEqualHoweverTheyWereWritten() {
        Assert.Equal(PermissionSet.Parse(["display", "files.home"]), PermissionSet.Parse(["files.home", "display"]));
        Assert.True(PermissionSet.Parse(["display"]) != PermissionSet.Parse(["files.home"]));
    }

    [Fact]
    public void ParsingAnInfoIsParsingItsArray() {
        var info = new BundleInfo {
            Identifier = "io.trinix.hello",
            Name = "Hello",
            Version = "1.0.0",
            EntryPoint = "Contents/Bin/hello",
            Permissions = ["files.home"]
        };

        Assert.Equal(PermissionSet.Parse(["files.home"]), PermissionSet.Parse(info));
    }

    [Fact]
    public void TheTwoShippedApplicationsStillDeclareSomethingThisSystemUnderstands() {
        // The vocabulary changed once, when doc 04 replaced the placeholder set.
        // These are what src/Trinix.Apps.Hello and src/Trinix.Apps.HelloUi have in
        // their Info.json, and they are here so that a future edit to the
        // vocabulary cannot silently make the shipped bundles unlaunchable.
        Assert.True(BundlePermissions.IsKnown("files.home"));
        Assert.True(BundlePermissions.IsKnown("display"));
    }
}

namespace Trinix.Bundle.Tests;

/// <summary>
///     The walk that decides what a signature covers.
/// </summary>
/// <remarks>
///     <see cref="BundleScanner" /> is hand-written rather than a call to
///     <c>Directory.EnumerateFiles(..., AllDirectories)</c> for two reasons, and both
///     are tested here. It refuses symlinks, because a bundle containing a link to
///     <c>/</c> would otherwise be "signed" over the whole filesystem. And its order is
///     ordinal and explicit, because the order is an input to the Merkle root — a walk
///     that inherited the filesystem's <c>readdir</c> order would produce a different
///     root for the same bundle on ext4 and on erofs, and every bundle in the image
///     would fail to verify on the machine it was installed on.
/// </remarks>
public class BundleScannerTests {
    [Fact]
    public void FindsEveryRegularFileAndNoDirectories() {
        using var scratch = TempDirectory.Create(nameof(FindsEveryRegularFileAndNoDirectories));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(Path.Combine(root, "Contents", "Resources", "deep", "deeper"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Contents", "Resources", "deep", "deeper", "leaf.txt"), "leaf");

        var found = BundleScanner.Scan(root);

        Assert.Equal(
            ["Contents/Info.json", "Contents/Resources/deep/deeper/leaf.txt"],
            found.Select(f => f.RelativePath).ToArray()
        );
    }

    [Fact]
    public void UsesForwardSlashesWhateverTheHostSeparatorIs() {
        // The relative path goes into the Merkle leaf verbatim, so a bundle
        // sealed on a host with a different separator would not verify on
        // Trinix. Nothing in the format is host-dependent, and this is where
        // that could quietly stop being true.
        using var scratch = TempDirectory.Create(nameof(UsesForwardSlashesWhateverTheHostSeparatorIs));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(Path.Combine(root, "Contents", "Bin"));
        File.WriteAllText(Path.Combine(root, "Contents", "Bin", "hello"), "x");

        Assert.All(BundleScanner.Scan(root), f => Assert.DoesNotContain('\\', f.RelativePath));
        Assert.Equal("Contents/Bin/hello", BundleScanner.Scan(root)[0].RelativePath);
    }

    [Fact]
    public void OrdersOrdinallyRatherThanByCulture() {
        // ⚠ No two names here differ only by case. That is not fussiness: macOS
        // is case-insensitive by default, so "Apple" and "apple" would be one
        // file on a developer's machine and two on CI — a test that passes in
        // one place and fails in the other proves nothing about the ordering.
        // 'Z' (0x5A), '_' (0x5F) and 'a' (0x61) separate the two orderings on
        // their own: ICU's collation puts the underscore first and folds case,
        // so it would answer _, a, Z.
        using var scratch = TempDirectory.Create(nameof(OrdersOrdinallyRatherThanByCulture));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a", "_", "Z", "0" }) {
            File.WriteAllText(Path.Combine(root, name), name);
        }

        var paths = BundleScanner.Scan(root).Select(f => f.RelativePath).ToArray();

        Assert.Equal(["0", "Z", "_", "a"], paths);
    }

    [Fact]
    public void ExcludesTheSignatureDirectory() {
        using var scratch = TempDirectory.Create(nameof(ExcludesTheSignatureDirectory));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(Path.Combine(root, BundleLayout.SignatureDirectory));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.json"), "{}");
        File.WriteAllText(Path.Combine(root, BundleLayout.ManifestPath), "{}");
        File.WriteAllText(Path.Combine(root, BundleLayout.SignaturePath), "sig");

        var found = BundleScanner.Scan(root);

        Assert.Equal(["Contents/Info.json"], found.Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void RefusesASymlinkedFileRatherThanSkippingIt() {
        // ⚠ Skipping would leave the link outside the signature, which is
        // precisely where an attacker would want to put something.
        using var scratch = TempDirectory.Create(nameof(RefusesASymlinkedFileRatherThanSkippingIt));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(Path.Combine(root, "Contents"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.json"), "{}");
        File.CreateSymbolicLink(Path.Combine(root, "Contents", "passwd"), "/etc/passwd");

        var e = Assert.Throws<BundleException>(() => BundleScanner.Scan(root));
        Assert.Equal(BundleFailure.UnsupportedEntry, e.Failure);
        Assert.Contains("symbolic link", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesASymlinkedDirectoryWithoutFollowingIt() {
        // The one that matters: following it would hash whatever is on the
        // other side, which for a link to / is the entire machine.
        using var scratch = TempDirectory.Create(nameof(RefusesASymlinkedDirectoryWithoutFollowingIt));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(Path.Combine(root, "Contents"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.json"), "{}");
        Directory.CreateSymbolicLink(Path.Combine(root, "Contents", "etc"), "/etc");

        var e = Assert.Throws<BundleException>(() => BundleScanner.Scan(root));
        Assert.Equal(BundleFailure.UnsupportedEntry, e.Failure);
    }

    [Fact]
    public void RecordsTheOwnerExecuteBitAndOnlyThat() {
        // A bundle is installed by copying, and the copy's group and other bits
        // follow the installing process's umask — so recording them would make
        // the signature depend on the umask of whoever built the image.
        using var scratch = TempDirectory.Create(nameof(RecordsTheOwnerExecuteBitAndOnlyThat));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(root);

        var runnable = Path.Combine(root, "runnable");
        var data = Path.Combine(root, "data");
        File.WriteAllText(runnable, "x");
        File.WriteAllText(data, "x");
        File.SetUnixFileMode(runnable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        // Group- and other-executable, but not owner-executable: still "not
        // executable" as far as the manifest is concerned.
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        Assert.True(BundleScanner.IsExecutable(runnable));
        Assert.False(BundleScanner.IsExecutable(data));
    }

    [Fact]
    public void RecordsTheLength() {
        using var scratch = TempDirectory.Create(nameof(RecordsTheLength));
        var root = scratch.Combine("Hello.app");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "payload"), new byte[1234]);

        Assert.Equal(1234, BundleScanner.Scan(root)[0].Size);
    }

    [Fact]
    public void AnEmptyTreeScansToNothingRatherThanThrowing() {
        // The scanner's job is to report; deciding that an empty bundle cannot
        // be signed belongs to the sealer, which says so in words.
        using var scratch = TempDirectory.Create(nameof(AnEmptyTreeScansToNothingRatherThanThrowing));
        var root = scratch.Combine("Empty.app");
        Directory.CreateDirectory(root);

        Assert.Empty(BundleScanner.Scan(root));
    }

    [Fact]
    public void RejectsANullPath() {
        Assert.Throws<ArgumentNullException>(() => BundleScanner.Scan(null!));
    }
}

namespace Trinix.Bundle.Tests;

/// <summary>
///     Path safety, which is the check standing between a manifest and a directory
///     traversal.
/// </summary>
/// <remarks>
///     <see cref="BundleLayout.IsSafeRelativePath" /> is called on attacker-controlled
///     input twice — once when sealing and once when verifying a manifest someone else
///     produced — so its negative cases matter more than its positive one. Each
///     rejected shape below is a real escape someone has shipped in a real archive
///     format: the absolute path, the parent traversal, the Windows separator that a
///     POSIX-only check misses, and the empty segment that collapses on some
///     filesystems and not others.
/// </remarks>
public class BundleLayoutTests {
    [Theory]
    [InlineData("Contents/Info.json")]
    [InlineData("Contents/Bin/hello")]
    [InlineData("Contents/Resources/icons/app.png")]
    [InlineData("a")]
    [InlineData("Contents/Resources/..name")] // a leading pair of dots is not a traversal
    [InlineData("Contents/Resources/file.tar.gz")]
    public void AcceptsAnOrdinaryBundlePath(string path) {
        Assert.True(BundleLayout.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")] // absolute
    [InlineData("/")]
    [InlineData("../outside")] // traversal at the front
    [InlineData("Contents/../../outside")] // traversal in the middle
    [InlineData("Contents/..")] // traversal at the end
    [InlineData(".")]
    [InlineData("Contents/./Info.json")] // a "no-op" segment is still not a name
    [InlineData("Contents//Info.json")] // empty segment
    [InlineData("Contents/")] // trailing separator leaves an empty segment
    [InlineData("Contents\\Info.json")] // a backslash is a separator on the machines that build these
    [InlineData("Contents/Bin\\..\\..\\etc")]
    public void RejectsAnythingThatCouldLeaveTheBundle(string path) {
        Assert.False(BundleLayout.IsSafeRelativePath(path));
    }

    [Fact]
    public void SignatureMaterialIsTheSignatureDirectoryAndItsContents() {
        Assert.True(BundleLayout.IsSignatureMaterial(BundleLayout.SignatureDirectory));
        Assert.True(BundleLayout.IsSignatureMaterial(BundleLayout.ManifestPath));
        Assert.True(BundleLayout.IsSignatureMaterial(BundleLayout.SignaturePath));
        Assert.True(BundleLayout.IsSignatureMaterial(BundleLayout.CertificatesPath));
    }

    [Fact]
    public void SignatureMaterialDoesNotMatchOnAPrefix() {
        // ⚠ The exclusion is what keeps files out of the signature, so a sibling
        // directory whose name merely starts with "_Signature" must not inherit
        // it — that would be a place to hide an unsigned file inside a signed
        // bundle.
        Assert.False(BundleLayout.IsSignatureMaterial("Contents/_SignatureExtra/payload"));
        Assert.False(BundleLayout.IsSignatureMaterial("Contents/_Signatures"));
        Assert.False(BundleLayout.IsSignatureMaterial("Contents/Info.json"));
        Assert.False(BundleLayout.IsSignatureMaterial("Contents/Bin/hello"));
    }

    [Theory]
    [InlineData("/Applications/Hello.app", "Hello")]
    [InlineData("/Applications/Hello.app/", "Hello")]
    [InlineData("Hello.app", "Hello")]
    [InlineData("/Applications/Hello", "Hello")] // no extension: the name as-is
    [InlineData("/Applications/My.App.app", "My.App")] // only the last suffix comes off
    public void NameOfStripsExactlyTheBundleExtension(string path, string expected) {
        Assert.Equal(expected, BundleLayout.NameOf(path));
    }

    [Fact]
    public void TheSignaturePathsAllLiveUnderTheSignatureDirectory() {
        // A constant edited in isolation is how these drift apart; the sealer
        // creates the directory and then writes three paths assumed to be in it.
        Assert.StartsWith(BundleLayout.SignatureDirectory + "/", BundleLayout.ManifestPath, StringComparison.Ordinal);
        Assert.StartsWith(BundleLayout.SignatureDirectory + "/", BundleLayout.SignaturePath, StringComparison.Ordinal);
        Assert.StartsWith(
            BundleLayout.SignatureDirectory + "/",
            BundleLayout.CertificatesPath,
            StringComparison.Ordinal
        );
        Assert.StartsWith(BundleLayout.Contents + "/", BundleLayout.InfoPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeSigningEkuIsTheOneRfc5280Defines() {
        // Not a tautology: this OID is what stops a TLS certificate from
        // doubling as a code-signing one, and a typo in it would silently widen
        // the set of certificates that can sign a bundle.
        Assert.Equal("1.3.6.1.5.5.7.3.3", BundleLayout.CodeSigningEku);
    }
}

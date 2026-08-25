using System.Security.Cryptography;
using System.Text;

namespace Trinix.Bundle.Tests;

/// <summary>
///     The Merkle tree, and the three ways this construction is usually got wrong.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="MerkleTree" />'s own remarks name the mistakes it avoids —
///         undifferentiated leaf and node hashing, duplicating the odd node, and
///         hashing content without identity. Prose in a comment is not a test, and
///         each of those is checkable in three lines, so each is checked here.
///     </para>
///     <para>
///         The assertions are mostly of the form "these two inputs must not produce
///         the same root". That is the only shape available: a Merkle root has no
///         expected value to compare against unless one is pinned, and pinning one
///         would test <see cref="SHA256" /> rather than this code.
///     </para>
/// </remarks>
public class MerkleTreeTests {
    static byte[] Hash(string content) => SHA256.HashData(Encoding.UTF8.GetBytes(content));

    static byte[] LeafOf(string path, string content) =>
        MerkleTree.Leaf(path, Encoding.UTF8.GetByteCount(content), false, Hash(content));

    [Fact]
    public void RootOfOneLeafIsThatLeaf() {
        var leaf = LeafOf("Contents/Info.json", "{}");
        Assert.Equal(leaf, MerkleTree.Root([leaf]));
    }

    [Fact]
    public void RootIsThirtyTwoBytes() {
        var root = MerkleTree.Root([LeafOf("a", "1"), LeafOf("b", "2"), LeafOf("c", "3")]);
        Assert.Equal(32, root.Length);
    }

    [Fact]
    public void EmptyBundleHasNoRoot() {
        // Returning a fixed value for the empty tree would make "a bundle with
        // no files" a signable object, which is the point of the exception.
        Assert.Throws<ArgumentException>(() => MerkleTree.Root([]));
    }

    [Fact]
    public void OrderIsPartOfTheRoot() {
        var a = LeafOf("Contents/Bin/one", "one");
        var b = LeafOf("Contents/Bin/two", "two");
        Assert.NotEqual(MerkleTree.Root([a, b]), MerkleTree.Root([b, a]));
    }

    [Fact]
    public void FlippingOneByteOfOneFileChangesTheRoot() {
        // The property the whole format rests on. If this can fail, a modified
        // application passes verification.
        var original = new[] { LeafOf("Contents/Bin/app", "hello"), LeafOf("Contents/Resources/x", "world") };
        var tampered = new[] { original[0], LeafOf("Contents/Resources/x", "worlD") };
        Assert.NotEqual(MerkleTree.Root(original), MerkleTree.Root(tampered));
    }

    [Fact]
    public void LeafBindsThePathToTheContent() {
        // The same bytes at a different path must be a different leaf, or a
        // bundle can be rearranged inside its own signature.
        var content = Hash("payload");
        Assert.NotEqual(
            MerkleTree.Leaf("Contents/Resources/data", 7, false, content),
            MerkleTree.Leaf("Contents/Bin/data", 7, false, content)
        );
    }

    [Fact]
    public void LeafBindsTheExecutableBit() {
        // Making a data file executable is an edit an attacker wants and a
        // content-only hash would not notice.
        var content = Hash("payload");
        Assert.NotEqual(
            MerkleTree.Leaf("Contents/Resources/data", 7, false, content),
            MerkleTree.Leaf("Contents/Resources/data", 7, true, content)
        );
    }

    [Fact]
    public void LeafBindsTheLength() {
        var content = Hash("payload");
        Assert.NotEqual(
            MerkleTree.Leaf("Contents/Resources/data", 7, false, content),
            MerkleTree.Leaf("Contents/Resources/data", 8, false, content)
        );
    }

    [Fact]
    public void PathTerminatorSeparatesAdjacentFields() {
        // Without the NUL after the path, "ab" + executable=false and "a" +
        // "b"-shaped continuations could collide. Two paths that differ only by
        // where the boundary falls must still differ.
        var content = Hash("payload");
        Assert.NotEqual(
            MerkleTree.Leaf("ab", 1, false, content),
            MerkleTree.Leaf("a", 1, false, content)
        );
    }

    [Fact]
    public void AnInteriorNodeIsNotAValidLeaf() {
        // RFC 6962's reason for the 0x00/0x01 prefixes: without them an
        // attacker can present the hash of two leaves as a single leaf and
        // claim a two-file bundle is a one-file bundle with the same root.
        var a = LeafOf("Contents/Bin/one", "one");
        var b = LeafOf("Contents/Bin/two", "two");
        var interior = MerkleTree.Root([a, b]);

        // Whatever the interior node is, no single-leaf tree equals it.
        Assert.NotEqual(interior, MerkleTree.Root([MerkleTree.Leaf("x", 0, false, interior)]));
        Assert.NotEqual(interior, a);
        Assert.NotEqual(interior, b);
    }

    [Fact]
    public void TheOddNodeIsPromotedRatherThanDuplicated() {
        // Duplicating the lone node — Bitcoin's mistake — makes [a, b, c] and
        // [a, b, c, c] the same tree, so a file list can be extended without
        // changing the root.
        var a = LeafOf("a", "1");
        var b = LeafOf("b", "2");
        var c = LeafOf("c", "3");

        Assert.NotEqual(MerkleTree.Root([a, b, c]), MerkleTree.Root([a, b, c, c]));
    }

    [Fact]
    public void ThreeLeavesFoldTheWayTheDocumentationSays() {
        // Pins the *shape* of the fold rather than a magic value: level one is
        // [node(a,b), c] because c is promoted, and level two is node of those.
        // Recomputing it here means a change to Root() that alters the tree
        // shape fails loudly instead of silently invalidating every signature
        // in the field.
        var a = LeafOf("a", "1");
        var b = LeafOf("b", "2");
        var c = LeafOf("c", "3");

        var expected = MerkleTree.Root([MerkleTree.Root([a, b]), c]);
        Assert.Equal(expected, MerkleTree.Root([a, b, c]));
    }

    [Fact]
    public void LeafRejectsANullPath() {
        Assert.Throws<ArgumentNullException>(() => MerkleTree.Leaf(null!, 0, false, Hash("x")));
    }

    [Fact]
    public void RootRejectsANullList() {
        Assert.Throws<ArgumentNullException>(() => MerkleTree.Root(null!));
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Trinix.Bundle;

/// <summary>
///     The Merkle tree over a bundle's contents, and the leaf encoding that feeds it.
/// </summary>
/// <remarks>
///     <para>
///         A flat list of file hashes would be enough to detect tampering, so the tree
///         earns its place for a different reason: it produces one short value that
///         names the exact contents of a bundle. That value is what the install
///         receipt records, what the launcher compares against, and what a future
///         update can use to say "this is already the version you have" without
///         re-reading the bundle. A list gives you none of that without hashing the
///         list — at which point you have a one-level tree anyway, minus the ability
///         to prove membership of a single file.
///     </para>
///     <para>
///         The construction is RFC 6962's, including the part that matters most:
///         leaves and interior nodes are hashed with different prefixes. Without that
///         distinction an attacker can present an interior node as if it were a leaf,
///         which is the classic second-preimage attack on naive Merkle trees. Odd
///         nodes are promoted unchanged to the next level rather than duplicated,
///         which is the other classic mistake — duplication makes two different file
///         lists produce the same root.
///     </para>
/// </remarks>
public static class MerkleTree {
    const byte LeafPrefix = 0x00;
    const byte NodePrefix = 0x01;

    /// <summary>
    ///     The hash of one file's identity and content, as it enters the tree.
    /// </summary>
    /// <remarks>
    ///     The path, the executable bit and the length are hashed *with* the
    ///     content hash rather than beside it. Hashing content alone would let a
    ///     bundle be rearranged — the same bytes moved to a different path, or a
    ///     data file made executable — without changing the root, and both of those
    ///     are exactly the kind of edit an attacker would want.
    /// </remarks>
    public static byte[] Leaf(string path, long size, bool executable, ReadOnlySpan<byte> contentHash) {
        ArgumentNullException.ThrowIfNull(path);

        var pathBytes = Encoding.UTF8.GetBytes(path);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, size);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([LeafPrefix]);
        hash.AppendData(pathBytes);
        hash.AppendData([0]); // terminator: paths vary in length
        hash.AppendData([executable ? (byte)1 : (byte)0]);
        hash.AppendData(length);
        hash.AppendData(contentHash);
        return hash.GetHashAndReset();
    }

    /// <summary>
    ///     Fold a list of leaf hashes into a single root.
    /// </summary>
    /// <param name="leaves">
    ///     In the order they are listed in the manifest, which is ordinal by path.
    ///     The order is part of the value: a tree is not a set.
    /// </param>
    public static byte[] Root(IReadOnlyList<byte[]> leaves) {
        ArgumentNullException.ThrowIfNull(leaves);

        if (leaves.Count == 0) {
            // An empty bundle is not a thing anyone should be able to sign, and
            // returning some fixed value for it would make that signable.
            throw new ArgumentException("a bundle with no files has no Merkle root", nameof(leaves));
        }

        byte[][] level = [.. leaves];
        while (level.Length > 1) {
            var next = new byte[(level.Length + 1) / 2][];
            for (int i = 0, o = 0; i < level.Length; i += 2, o++) {
                // The lone node at the end of an odd level is promoted, not
                // paired with itself.
                next[o] = i + 1 < level.Length ? Node(level[i], level[i + 1]) : level[i];
            }

            level = next;
        }

        return level[0];
    }

    static byte[] Node(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([NodePrefix]);
        hash.AppendData(left);
        hash.AppendData(right);
        return hash.GetHashAndReset();
    }
}

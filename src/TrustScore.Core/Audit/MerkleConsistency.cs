using System.Security.Cryptography;

namespace TrustScore.Core.Audit;

/// <summary>
/// Consistency proofs between two v2 trees: evidence that the tree of the first <c>m</c> leaves is
/// a prefix of the tree of <c>n</c> leaves, i.e. that the later anchor only appended to the earlier
/// one and did not drop, edit or reorder anything it had already committed to.
///
/// A v2 root is RFC 6962's Merkle Tree Hash, so this is the standard algorithm: proof generation
/// from RFC 6962 §2.1.2, verification from RFC 9162 §2.1.4.2. Only defined for v2 trees; a v1 tree
/// hashes nodes differently and pads odd levels, so no v1 root can be proven consistent with
/// anything.
/// </summary>
public static class MerkleConsistency
{
    private const byte NodePrefix = 0x01;

    /// <summary>
    /// The proof that the first <paramref name="oldSize"/> leaves of <paramref name="leaves"/>
    /// form a tree that the whole list extends. Empty when the sizes are equal.
    /// </summary>
    public static List<byte[]> Prove(IReadOnlyList<byte[]> leaves, int oldSize)
    {
        if (oldSize < 1 || oldSize > leaves.Count)
            throw new ArgumentOutOfRangeException(nameof(oldSize));

        var proof = new List<byte[]>();
        SubProof(oldSize, leaves, 0, leaves.Count, isOriginalTree: true, proof);
        return proof;
    }

    // RFC 6962 §2.1.2 SUBPROOF(m, D[start:start+count], b).
    private static void SubProof(
        int m, IReadOnlyList<byte[]> leaves, int start, int count, bool isOriginalTree, List<byte[]> proof)
    {
        if (m == count)
        {
            if (!isOriginalTree)
                proof.Add(Root(leaves, start, count));
            return;
        }

        var k = LargestPowerOfTwoBelow(count);
        if (m <= k)
        {
            SubProof(m, leaves, start, k, isOriginalTree, proof);
            proof.Add(Root(leaves, start + k, count - k));
        }
        else
        {
            SubProof(m - k, leaves, start + k, count - k, isOriginalTree: false, proof);
            proof.Add(Root(leaves, start, k));
        }
    }

    /// <summary>
    /// RFC 9162 §2.1.4.2: true when <paramref name="proof"/> shows the tree of size
    /// <paramref name="oldSize"/> with root <paramref name="oldRoot"/> is a prefix of the tree of
    /// size <paramref name="newSize"/> with root <paramref name="newRoot"/>.
    ///
    /// The sizes are inputs, not outputs: the algorithm only tells sizes apart where they change
    /// the shape of the path, so a proof for 9 -> 23 also passes as 9 -> 22. The sizes must come
    /// from the same place as the roots (a published anchor pairs each root with its leaf count),
    /// exactly as Certificate Transparency takes them from a signed tree head.
    /// </summary>
    public static bool Verify(
        long oldSize, long newSize, byte[] oldRoot, byte[] newRoot, IReadOnlyList<byte[]> proof)
    {
        if (oldSize < 1 || oldSize > newSize)
            return false;

        if (oldSize == newSize)
            return proof.Count == 0 && oldRoot.AsSpan().SequenceEqual(newRoot);

        var path = new List<byte[]>(proof);
        if (path.Count == 0)
            return false;

        // An old tree whose size is a power of two is a complete subtree of the new one, so its
        // root is the starting node and the proof does not repeat it.
        if ((oldSize & (oldSize - 1)) == 0)
            path.Insert(0, oldRoot);

        var fn = oldSize - 1;
        var sn = newSize - 1;
        while ((fn & 1) == 1)
        {
            fn >>= 1;
            sn >>= 1;
        }

        var fr = path[0];
        var sr = path[0];
        foreach (var c in path.Skip(1))
        {
            if (sn == 0)
                return false;

            if ((fn & 1) == 1 || fn == sn)
            {
                fr = Node(c, fr);
                sr = Node(c, sr);
                if ((fn & 1) == 0)
                {
                    while ((fn & 1) == 0 && fn != 0)
                    {
                        fn >>= 1;
                        sn >>= 1;
                    }
                }
            }
            else
            {
                sr = Node(sr, c);
            }

            fn >>= 1;
            sn >>= 1;
        }

        return sn == 0 && fr.AsSpan().SequenceEqual(oldRoot) && sr.AsSpan().SequenceEqual(newRoot);
    }

    /// <summary>
    /// The direct check the anchoring job runs: the first <paramref name="oldSize"/> of today's
    /// leaves still hash to the root that was anchored for them.
    /// </summary>
    public static bool Extends(IReadOnlyList<byte[]> leaves, int oldSize, byte[] oldRoot) =>
        oldSize >= 1 && oldSize <= leaves.Count && Root(leaves, 0, oldSize).AsSpan().SequenceEqual(oldRoot);

    /// <summary>RFC 6962 MTH over leaves[start, start+count), equal to a v2 <see cref="MerkleTree"/> root.</summary>
    public static byte[] Root(IReadOnlyList<byte[]> leaves, int start, int count)
    {
        if (count == 1)
            return leaves[start];
        var k = LargestPowerOfTwoBelow(count);
        return Node(Root(leaves, start, k), Root(leaves, start + k, count - k));
    }

    private static int LargestPowerOfTwoBelow(int n)
    {
        var k = 1;
        while (k * 2 < n) k *= 2;
        return k;
    }

    private static byte[] Node(byte[] left, byte[] right)
    {
        var buffer = new byte[1 + left.Length + right.Length];
        buffer[0] = NodePrefix;
        left.CopyTo(buffer, 1);
        right.CopyTo(buffer, 1 + left.Length);
        return SHA256.HashData(buffer);
    }
}

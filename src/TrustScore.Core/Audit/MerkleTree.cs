using System.Security.Cryptography;
using System.Text;

namespace TrustScore.Core.Audit;

/// <summary>
/// Append-only Merkle tree for audit logging.
/// Each leaf is SHA256(rating_id + service_did + timestamp).
/// Provides inclusion proofs to verify a rating exists in the tree.
/// </summary>
public sealed class MerkleTree
{
    private readonly List<byte[]> _leaves = new();

    public int LeafCount => _leaves.Count;

    /// <summary>
    /// Current root hash of the tree. Null if empty.
    /// </summary>
    public byte[]? Root => _leaves.Count == 0 ? null : ComputeRoot(_leaves);

    /// <summary>
    /// Root hash as a hex string.
    /// </summary>
    public string? RootHex => Root is null ? null : Convert.ToHexString(Root).ToLowerInvariant();

    /// <summary>
    /// Add a rating as a leaf to the tree.
    /// </summary>
    public void AddLeaf(Guid ratingId, string serviceDid, DateTimeOffset timestamp)
    {
        var data = $"{ratingId}:{serviceDid}:{timestamp:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        _leaves.Add(hash);
    }

    /// <summary>
    /// Add a pre-computed hash as a leaf.
    /// </summary>
    public void AddLeafHash(byte[] hash)
    {
        _leaves.Add(hash);
    }

    /// <summary>
    /// Compute the leaf hash for a rating (without adding it to the tree).
    /// </summary>
    public static byte[] ComputeLeafHash(Guid ratingId, string serviceDid, DateTimeOffset timestamp)
    {
        var data = $"{ratingId}:{serviceDid}:{timestamp:O}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>
    /// Generate an inclusion proof for a leaf at the given index.
    /// Returns a list of (hash, isRight) pairs that can be used to reconstruct the root.
    /// </summary>
    public List<ProofNode> GetInclusionProof(int leafIndex)
    {
        if (leafIndex < 0 || leafIndex >= _leaves.Count)
            throw new ArgumentOutOfRangeException(nameof(leafIndex));

        var proof = new List<ProofNode>();
        var currentLevel = _leaves.ToList();
        var index = leafIndex;

        while (currentLevel.Count > 1)
        {
            // If odd number of nodes, duplicate the last one
            if (currentLevel.Count % 2 == 1)
                currentLevel.Add(currentLevel[^1]);

            var sibling = (index % 2 == 0) ? index + 1 : index - 1;
            var isRight = index % 2 == 0; // sibling is on the right

            proof.Add(new ProofNode(currentLevel[sibling], isRight));

            // Move to next level
            var nextLevel = new List<byte[]>();
            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                nextLevel.Add(HashPair(currentLevel[i], currentLevel[i + 1]));
            }

            currentLevel = nextLevel;
            index /= 2;
        }

        return proof;
    }

    /// <summary>
    /// Verify an inclusion proof against a root hash.
    /// </summary>
    public static bool VerifyProof(byte[] leafHash, List<ProofNode> proof, byte[] expectedRoot)
    {
        var current = leafHash;

        foreach (var node in proof)
        {
            current = node.IsRight
                ? HashPair(current, node.Hash)
                : HashPair(node.Hash, current);
        }

        return current.SequenceEqual(expectedRoot);
    }

    private static byte[] ComputeRoot(List<byte[]> leaves)
    {
        if (leaves.Count == 0)
            throw new InvalidOperationException("Cannot compute root of empty tree");

        var currentLevel = leaves.ToList();

        while (currentLevel.Count > 1)
        {
            // If odd number of nodes, duplicate the last one
            if (currentLevel.Count % 2 == 1)
                currentLevel.Add(currentLevel[^1]);

            var nextLevel = new List<byte[]>();
            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                nextLevel.Add(HashPair(currentLevel[i], currentLevel[i + 1]));
            }

            currentLevel = nextLevel;
        }

        return currentLevel[0];
    }

    private static byte[] HashPair(byte[] left, byte[] right)
    {
        var combined = new byte[left.Length + right.Length];
        left.CopyTo(combined, 0);
        right.CopyTo(combined, left.Length);
        return SHA256.HashData(combined);
    }
}

/// <summary>
/// A node in an inclusion proof.
/// </summary>
public sealed record ProofNode(byte[] Hash, bool IsRight)
{
    public string HashHex => Convert.ToHexString(Hash).ToLowerInvariant();
}

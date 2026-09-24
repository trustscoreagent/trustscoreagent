using System.Security.Cryptography;
using System.Text;

namespace TrustScore.Core.Audit;

/// <summary>
/// How interior nodes are built. Stored with every anchor, because a root can only be reproduced
/// (and a proof only verified) with the algorithm that produced it.
/// </summary>
public enum MerkleTreeVersion
{
    /// <summary>
    /// Original scheme, kept to reproduce anchors made with it: node = SHA256(left || right), and
    /// an odd node is paired with a copy of itself. That duplication lets two different leaf lists
    /// share a root (the CVE-2012-2459 pattern), and with no leaf/node prefix an interior node can
    /// be passed off as a leaf.
    /// </summary>
    V1 = 1,

    /// <summary>
    /// RFC 6962-style: node = SHA256(0x01 || left || right), leaves carry their own 0x00 prefix,
    /// and an odd node is promoted unchanged to the next level. The resulting root equals RFC 6962
    /// §2.1 MTH for every leaf count.
    /// </summary>
    V2 = 2,
}

/// <summary>
/// Append-only Merkle tree for the audit log. Leaves are 32-byte hashes computed elsewhere (see
/// <see cref="RatingLeaf"/>); this class only combines them, under a given
/// <see cref="MerkleTreeVersion"/>.
/// </summary>
public sealed class MerkleTree
{
    private const byte NodePrefix = 0x01;

    private readonly List<byte[]> _leaves = new();

    public MerkleTree(MerkleTreeVersion version = MerkleTreeVersion.V1)
    {
        if (!Enum.IsDefined(version))
            throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
    }

    public MerkleTreeVersion Version { get; }

    public int LeafCount => _leaves.Count;

    /// <summary>
    /// Current root hash of the tree. Null if empty.
    /// </summary>
    public byte[]? Root => _leaves.Count == 0 ? null : ComputeRoot(_leaves, Version);

    /// <summary>
    /// Root hash as a hex string.
    /// </summary>
    public string? RootHex => Root is null ? null : Convert.ToHexString(Root).ToLowerInvariant();

    /// <summary>
    /// Add a rating as a v1 leaf. Only meaningful for reproducing v1 data.
    /// </summary>
    public void AddLeaf(Guid ratingId, string serviceDid, DateTimeOffset timestamp)
    {
        _leaves.Add(ComputeLeafHash(ratingId, serviceDid, timestamp));
    }

    /// <summary>
    /// Add a pre-computed hash as a leaf.
    /// </summary>
    public void AddLeafHash(byte[] hash)
    {
        _leaves.Add(hash);
    }

    /// <summary>
    /// The v1 leaf hash: SHA256 of "{id}:{service}:{timestamp:O}". It commits to the rating's
    /// existence only, not to what it reported. New leaves use <see cref="RatingLeaf"/> v2.
    /// </summary>
    public static byte[] ComputeLeafHash(Guid ratingId, string serviceDid, DateTimeOffset timestamp)
    {
        var data = $"{ratingId}:{serviceDid}:{timestamp:O}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>
    /// Generate an inclusion proof for a leaf at the given index: the sibling hashes from the leaf
    /// up to the root, each flagged with the side it sits on. Under v2 a level where the node has
    /// no sibling (it is promoted) contributes nothing, so proof lengths vary.
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
            if (Version == MerkleTreeVersion.V1 && currentLevel.Count % 2 == 1)
                currentLevel.Add(currentLevel[^1]);

            var sibling = index % 2 == 0 ? index + 1 : index - 1;
            if (sibling < currentLevel.Count)
                proof.Add(new ProofNode(currentLevel[sibling], IsRight: index % 2 == 0));

            currentLevel = NextLevel(currentLevel, Version);
            index /= 2;
        }

        return proof;
    }

    /// <summary>
    /// Verify an inclusion proof against a root hash, under the tree version that produced it.
    /// </summary>
    public static bool VerifyProof(
        byte[] leafHash, List<ProofNode> proof, byte[] expectedRoot,
        MerkleTreeVersion version = MerkleTreeVersion.V1)
    {
        var current = leafHash;

        foreach (var node in proof)
        {
            current = node.IsRight
                ? HashPair(current, node.Hash, version)
                : HashPair(node.Hash, current, version);
        }

        return current.SequenceEqual(expectedRoot);
    }

    private static byte[] ComputeRoot(List<byte[]> leaves, MerkleTreeVersion version)
    {
        if (leaves.Count == 0)
            throw new InvalidOperationException("Cannot compute root of empty tree");

        var currentLevel = leaves.ToList();
        while (currentLevel.Count > 1)
        {
            if (version == MerkleTreeVersion.V1 && currentLevel.Count % 2 == 1)
                currentLevel.Add(currentLevel[^1]);
            currentLevel = NextLevel(currentLevel, version);
        }

        return currentLevel[0];
    }

    // Pairs nodes left to right. A trailing unpaired node (v2 only; v1 has already duplicated it)
    // is carried up unchanged.
    private static List<byte[]> NextLevel(List<byte[]> level, MerkleTreeVersion version)
    {
        var next = new List<byte[]>((level.Count + 1) / 2);
        for (var i = 0; i < level.Count; i += 2)
        {
            next.Add(i + 1 < level.Count
                ? HashPair(level[i], level[i + 1], version)
                : level[i]);
        }
        return next;
    }

    private static byte[] HashPair(byte[] left, byte[] right, MerkleTreeVersion version)
    {
        var prefix = version == MerkleTreeVersion.V2 ? 1 : 0;
        var combined = new byte[prefix + left.Length + right.Length];
        if (prefix == 1) combined[0] = NodePrefix;
        left.CopyTo(combined, prefix);
        right.CopyTo(combined, prefix + left.Length);
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

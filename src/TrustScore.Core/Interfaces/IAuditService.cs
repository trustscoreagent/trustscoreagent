using TrustScore.Core.Audit;

namespace TrustScore.Core.Interfaces;

public interface IAuditService
{
    /// <summary>
    /// Get the latest anchored Merkle root.
    /// </summary>
    Task<MerkleAnchor?> GetLatestAnchorAsync();

    /// <summary>
    /// Generate an inclusion proof for a specific rating.
    /// Rebuilds the Merkle tree from all leaf hashes and returns the proof.
    /// </summary>
    Task<InclusionProofResult?> GetInclusionProofAsync(Guid ratingId);

    /// <summary>Anchors, newest first, optionally only those older than <paramref name="beforeId"/>.</summary>
    Task<IReadOnlyList<MerkleAnchor>> GetAnchorsAsync(int limit, int? beforeId);

    /// <summary>
    /// Proof that anchor <paramref name="fromId"/>'s tree is a prefix of anchor
    /// <paramref name="toId"/>'s. Both must be v2 trees.
    /// </summary>
    Task<ConsistencyProofResult> GetConsistencyProofAsync(int fromId, int toId);
}

public enum ConsistencyProofStatus
{
    Ok,

    /// <summary>One of the anchors does not exist.</summary>
    AnchorNotFound,

    /// <summary>A v1 anchor, or a "from" anchor larger than the "to" one: no proof can exist.</summary>
    Unsupported,

    /// <summary>The later tree does not extend the earlier one: covered ratings changed.</summary>
    NotConsistent,

    /// <summary>The later anchor's leaves could not be reproduced, so no proof can be built now.</summary>
    SnapshotUnavailable,
}

public sealed class ConsistencyProofResult
{
    public ConsistencyProofStatus Status { get; init; }
    public MerkleAnchor? From { get; init; }
    public MerkleAnchor? To { get; init; }
    public IReadOnlyList<string> Proof { get; init; } = Array.Empty<string>();
}

public sealed class InclusionProofResult
{
    public required string RatingId { get; init; }
    public required string LeafHash { get; init; }

    /// <summary>The rating's committed fields as stored today, for recomputing the leaf independently.</summary>
    public required RatingLeaf Leaf { get; init; }

    public MerkleTreeVersion TreeVersion { get; init; }
    public required string MerkleRoot { get; init; }
    public required List<ProofNodeDto> Proof { get; init; }
    public int LeafIndex { get; init; }
    public int TotalLeaves { get; init; }
}

public sealed record ProofNodeDto(string Hash, bool IsRight);

public sealed class MerkleAnchor
{
    public int Id { get; init; }
    public required string MerkleRoot { get; init; }
    public int LeafCount { get; init; }
    public DateTimeOffset AnchoredAt { get; init; }
    // The created_at cutoff the anchored set was taken at. Null for legacy anchors, which fall back
    // to leaf_count-based reproduction.
    public DateTimeOffset? CutoffAt { get; init; }

    /// <summary>The algorithm the root was built with; the proof must be verified under the same one.</summary>
    public int TreeVersion { get; init; } = 1;
    public string? Blockchain { get; init; }
    public string? ContractAddress { get; init; }
    public string? TransactionHash { get; init; }
    public long? BlockNumber { get; init; }
}

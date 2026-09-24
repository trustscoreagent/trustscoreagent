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

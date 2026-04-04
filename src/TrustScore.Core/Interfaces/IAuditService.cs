namespace TrustScore.Core.Interfaces;

public interface IAuditService
{
    /// <summary>
    /// Record a rating's leaf hash for future Merkle tree inclusion.
    /// Called at rating insertion time.
    /// </summary>
    Task RecordLeafAsync(Guid ratingId, string serviceDid, DateTimeOffset timestamp);

    /// <summary>
    /// Get the latest anchored Merkle root.
    /// </summary>
    Task<MerkleAnchor?> GetLatestAnchorAsync();
}

public sealed class MerkleAnchor
{
    public int Id { get; init; }
    public required string MerkleRoot { get; init; }
    public int LeafCount { get; init; }
    public DateTimeOffset AnchoredAt { get; init; }
    public string? Blockchain { get; init; }
    public string? ContractAddress { get; init; }
    public string? TransactionHash { get; init; }
    public long? BlockNumber { get; init; }
}

namespace TrustScore.Core.Audit;

/// <summary>
/// A rating's audit leaf as it sits in the database: the committed fields as they read today, plus
/// the leaf hash written when the rating was inserted.
///
/// Which of the two goes into the tree depends on the leaf version:
/// <list type="bullet">
/// <item>v2: the hash stored at insertion. A row edited after the fact is not silently re-committed
/// with its new content; its original hash stays in the tree, and anyone recomputing the leaf from
/// the fields a proof returns sees the mismatch on that one rating. <see cref="IsIntact"/> is the
/// same check, run by the anchoring job.</item>
/// <item>v1: the hash recomputed from (id, service, created_at), because that is what every v1
/// anchor committed. The stored v1 hash was never used for anchoring, and some are stale (migration
/// 004 rewrote service ids of early ratings after they were hashed).</item>
/// </list>
/// </summary>
public sealed record StoredLeaf(
    Guid Id,
    string ServiceDid,
    DateTimeOffset CreatedAt,
    int LeafVersion,
    int StatusCode,
    int LatencyMs,
    int? ResponseSizeBytes,
    bool? SchemaValid,
    int? QualityScore,
    bool HasReceipt,
    bool ReceiptVerified,
    bool SignatureVerified,
    double Weight,
    string MerkleLeafHash)
{
    public RatingLeaf Leaf => new(
        Id, ServiceDid, CreatedAt, LeafVersion, StatusCode, LatencyMs, ResponseSizeBytes,
        SchemaValid, QualityScore, HasReceipt, ReceiptVerified, SignatureVerified, Weight);

    /// <summary>The hash this rating contributes to the tree (see the rules above).</summary>
    public byte[] AnchoredHash() =>
        LeafVersion == 1 ? Leaf.Hash() : Convert.FromHexString(MerkleLeafHash);

    public string AnchoredHashHex() => Convert.ToHexString(AnchoredHash()).ToLowerInvariant();

    /// <summary>
    /// True when a v2 row's current fields still hash to the leaf stored at insertion. v1 leaves are
    /// always recomputed, so there is nothing to compare them against.
    /// </summary>
    public bool IsIntact() =>
        LeafVersion == 1
        || string.Equals(Leaf.HashHex(), MerkleLeafHash, StringComparison.OrdinalIgnoreCase);
}

using System.Data;
using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IRatingRepository
{
    Task InsertAsync(Rating rating);

    /// <summary>Insert a rating (with its Merkle leaf hash) inside an existing transaction.</summary>
    Task InsertAsync(IDbConnection conn, IDbTransaction tx, Rating rating);
    Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window);
    Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId);
    Task<IReadOnlyList<RatingLeafInfo>> GetAllLeafHashesAsync();

    /// <summary>
    /// The first <paramref name="leafCount"/> leaves in deterministic (created_at, id) order —
    /// i.e. the exact snapshot a Merkle anchor of that leaf count was computed over.
    /// </summary>
    Task<IReadOnlyList<RatingLeafInfo>> GetAnchoredLeafHashesAsync(int leafCount);
    Task<IReadOnlyList<RatingSummary>> GetHistoryAsync(string serviceDid, int months);

    /// <summary>Daily aggregates computed in SQL (bounded transfer, no per-rating load).</summary>
    Task<IReadOnlyList<DailyHistoryPoint>> GetDailyHistoryAsync(string serviceDid, int months);
    Task<IReadOnlyList<AgentRatingRecord>> GetAllRatingsForTrustAsync();
}

public sealed record DailyHistoryPoint(
    DateTime Date,
    int RatingsCount,
    int AvgLatencyMs,
    double SuccessRate,
    double AvgQuality,
    int VerifiedCount);

/// <summary>
/// Minimal rating record used for EigenTrust computation.
/// </summary>
public sealed record AgentRatingRecord(
    string AgentDid,
    string ServiceDid,
    int StatusCode,
    int LatencyMs,
    bool? SchemaValid,
    bool ReceiptVerified);

public sealed record RatingLeafInfo(Guid Id, string ServiceDid, DateTimeOffset CreatedAt, string? MerkleLeafHash);

public sealed record RatingSummary(
    DateTimeOffset CreatedAt,
    int StatusCode,
    int LatencyMs,
    bool? SchemaValid,
    int? QualityScore,
    bool HasReceipt,
    bool ReceiptVerified,
    double Weight);

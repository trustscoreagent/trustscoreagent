using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IRatingRepository
{
    Task InsertAsync(Rating rating);
    Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window);
    Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId);
    Task<IReadOnlyList<RatingLeafInfo>> GetAllLeafHashesAsync();
    Task<IReadOnlyList<RatingSummary>> GetHistoryAsync(string serviceDid, int months);
}

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

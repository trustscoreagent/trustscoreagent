using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IRatingRepository
{
    Task InsertAsync(Rating rating);
    Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window);
    Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId);
    Task<IReadOnlyList<RatingLeafInfo>> GetAllLeafHashesAsync();
}

public sealed record RatingLeafInfo(Guid Id, string ServiceDid, DateTimeOffset CreatedAt, string? MerkleLeafHash);

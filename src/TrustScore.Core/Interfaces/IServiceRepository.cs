using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IServiceRepository
{
    Task<ServiceEntity?> GetByDidAsync(string did);
    Task<IReadOnlyList<ServiceEntity>> ListAsync(ServiceListFilter filter);
    Task UpsertAsync(ServiceEntity service);
    Task ApplyRatingAtomicAsync(string did, RatingDelta delta);
    Task<bool> ExistsAsync(string did);
}

public sealed class ServiceListFilter
{
    public int Limit { get; init; } = 20;
    public int Offset { get; init; } = 0;
    public string? SortBy { get; init; } = "score";   // "score", "ratings_count", "last_rated"
    public string? Order { get; init; } = "desc";      // "asc", "desc"
    public double? MinScore { get; init; }
    public int? MinRatings { get; init; }
}

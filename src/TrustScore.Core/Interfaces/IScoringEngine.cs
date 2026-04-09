using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IScoringEngine
{
    ServiceScore CalculateScore(ServiceEntity service);
    ServiceScore CalculateProviderScore(string provider, IReadOnlyList<ServiceEntity> endpoints);
    ServiceEntity ApplyRating(ServiceEntity service, Rating rating);
    RatingDelta ComputeDelta(Rating rating);
}

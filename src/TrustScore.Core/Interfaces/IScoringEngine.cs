using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IScoringEngine
{
    ServiceScore CalculateScore(ServiceEntity service);
    ServiceEntity ApplyRating(ServiceEntity service, Rating rating);
    RatingDelta ComputeDelta(Rating rating);
}

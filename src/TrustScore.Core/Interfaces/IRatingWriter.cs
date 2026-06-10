using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

/// <summary>
/// Persists a rating submission as a single atomic unit: the aggregate score update and the
/// rating record (carrying its Merkle audit leaf) either both commit or both roll back.
/// </summary>
public interface IRatingWriter
{
    Task SubmitAsync(string serviceId, RatingDelta delta, Rating rating);
}

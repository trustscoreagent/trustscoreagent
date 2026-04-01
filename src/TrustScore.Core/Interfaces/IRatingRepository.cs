using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IRatingRepository
{
    Task InsertAsync(Rating rating);
    Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window);
}

namespace TrustScore.Core.Interfaces;

/// <summary>
/// Counts API calls per day, endpoint and client family, to tell whether anyone besides us uses
/// the registry. Only aggregate numbers are kept: no IP, no agent DID, no request content.
/// </summary>
public interface IUsageCounter
{
    /// <summary>Records one call. Never throws: losing a count must not fail a request.</summary>
    Task RecordAsync(DateOnly day, string endpoint, string client);

    /// <summary>Counts for each (endpoint, client) pair over the given days, summed.</summary>
    Task<IReadOnlyList<UsageCount>> ReadAsync(DateOnly from, DateOnly to);
}

public sealed record UsageCount(string Endpoint, string Client, long Calls);

/// <summary>What the ratings table says about who rated over a period.</summary>
public sealed record RatingStats(
    long Total,
    long FromProbe,
    long FromAgents,
    long DistinctAgents,
    long Signed,
    long WithVerifiedReceipt);

public interface IUsageStatsRepository
{
    Task<RatingStats> GetRatingStatsAsync(DateTimeOffset since, string probeAgentDid);
}

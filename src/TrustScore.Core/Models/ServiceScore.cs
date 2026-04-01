namespace TrustScore.Core.Models;

public sealed class ServiceScore
{
    public required string ServiceDid { get; init; }
    public double Score { get; init; }
    public double Confidence { get; init; }
    public int RatingsCount { get; init; }
    public DimensionScores Dimensions { get; init; } = new();
    public int RecentIncidents { get; init; }
    public DateTimeOffset? LastRatedAt { get; init; }
    public bool ServiceSupportsReceipts { get; init; }
}

public sealed class DimensionScores
{
    public double Availability { get; init; }
    public double Latency { get; init; }
    public double Conformity { get; init; }
}

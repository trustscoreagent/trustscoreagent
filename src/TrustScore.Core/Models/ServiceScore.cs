namespace TrustScore.Core.Models;

public sealed class ServiceScore
{
    public required string ServiceDid { get; init; }
    public double Score { get; init; }
    public double Confidence { get; init; }
    public int RatingsCount { get; init; }
    public DimensionScores Dimensions { get; init; } = new();
    /// <summary>
    /// Null while incidents are not tracked. It used to be a hard-coded 0, which clients
    /// displayed as "no recent incidents": a claim nothing had measured.
    /// </summary>
    public int? RecentIncidents { get; init; }
    public DateTimeOffset? LastRatedAt { get; init; }
    public bool ServiceSupportsReceipts { get; init; }
}

public sealed class DimensionScores
{
    public double Availability { get; init; }
    public double Latency { get; init; }
    public double Conformity { get; init; }
}

namespace TrustScore.Core.Models;

public sealed class Rating
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ServiceDid { get; init; }
    public required string AgentDid { get; init; }
    public required RatingMetrics Metrics { get; init; }
    public int? QualityScore { get; init; }
    public string? Comment { get; init; }
    public string? Receipt { get; init; }
    public bool HasReceipt { get; init; }
    public bool ReceiptVerified { get; init; }
    public double Weight { get; init; } = 0.3;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class RatingMetrics
{
    public int StatusCode { get; init; }
    public int LatencyMs { get; init; }
    public int? ResponseSizeBytes { get; init; }
    public bool? SchemaValid { get; init; }
}

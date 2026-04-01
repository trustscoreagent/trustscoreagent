namespace TrustScore.Core.Models;

public sealed class ServiceEntity
{
    public required string Did { get; set; }
    public double Alpha { get; set; } = 1.0;
    public double Beta { get; set; } = 1.0;
    public double AlphaAvailability { get; set; } = 1.0;
    public double BetaAvailability { get; set; } = 1.0;
    public double AlphaLatency { get; set; } = 1.0;
    public double BetaLatency { get; set; } = 1.0;
    public double AlphaConformity { get; set; } = 1.0;
    public double BetaConformity { get; set; } = 1.0;
    public int RatingsCount { get; set; }
    public bool SupportsReceipts { get; set; }
    public DateTimeOffset? LastRatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

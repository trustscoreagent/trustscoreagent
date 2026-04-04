namespace TrustScore.Core.Models;

/// <summary>
/// Represents the atomic delta to apply to a service's reputation scores.
/// Applied via SQL UPDATE with arithmetic, avoiding read-then-write race conditions.
/// </summary>
public sealed class RatingDelta
{
    public double AlphaDelta { get; init; }
    public double BetaDelta { get; init; }
    public double AlphaAvailabilityDelta { get; init; }
    public double BetaAvailabilityDelta { get; init; }
    public double AlphaLatencyDelta { get; init; }
    public double BetaLatencyDelta { get; init; }
    public double AlphaConformityDelta { get; init; }
    public double BetaConformityDelta { get; init; }
    public bool SupportsReceipts { get; init; }
}

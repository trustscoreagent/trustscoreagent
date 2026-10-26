using Microsoft.Extensions.Configuration;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Scoring;

public sealed class BetaReputationSystem : IScoringEngine
{
    private readonly double _halfLifeDays;
    private readonly int _latencyThresholdMs;

    /// <summary>
    /// Evidence loses half its weight every this many days. Forgetting follows elapsed time, not
    /// the number of ratings: decaying by a fixed factor on every rating let anyone erase a
    /// service's history simply by rating it often.
    /// </summary>
    public const double DefaultHalfLifeDays = 30.0;

    private const double AvailabilityWeight = 0.4;
    private const double LatencyWeight = 0.35;
    private const double ConformityWeight = 0.25;

    /// <summary>
    /// The displayed score as a SQL expression over the services table, built from the same weights
    /// as <see cref="CalculateScore"/>. Sorting and min_score filtering must use this, not the
    /// aggregate alpha/(alpha+beta): that is a different number, and listing by it put services out
    /// of the order of the scores it displayed.
    /// </summary>
    public static readonly string ScoreSql = string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "({0} * {3} + {1} * {4} + {2} * {5})",
        AvailabilityWeight, LatencyWeight, ConformityWeight,
        DimensionSql("alpha_availability", "beta_availability"),
        DimensionSql("alpha_latency", "beta_latency"),
        DimensionSql("alpha_conformity", "beta_conformity"));

    // Mirrors BetaScore: alpha / (alpha + beta), or 0.5 when there is no evidence at all.
    private static string DimensionSql(string alpha, string beta) =>
        $"COALESCE({alpha} / NULLIF({alpha} + {beta}, 0), 0.5)";

    // Smoothing constant for the confidence curve: confidence reaches 0.5 at this many effective
    // observations. Higher = more evidence required before the score is treated as confident.
    private const double ConfidenceSmoothing = 10.0;

    // Verify weights sum to 1.0 at compile time
    static BetaReputationSystem()
    {
        var sum = AvailabilityWeight + LatencyWeight + ConformityWeight;
        if (Math.Abs(sum - 1.0) > 0.0001)
            throw new InvalidOperationException($"Dimension weights must sum to 1.0, got {sum}");
    }

    public BetaReputationSystem(IConfiguration? config = null)
    {
        _halfLifeDays = config?.GetValue<double>("Scoring:HalfLifeDays", DefaultHalfLifeDays) ?? DefaultHalfLifeDays;
        _latencyThresholdMs = config?.GetValue<int>("Scoring:LatencyThresholdMs", 2000) ?? 2000;
    }

    public ServiceScore CalculateScore(ServiceEntity service)
    {
        var availability = BetaScore(service.AlphaAvailability, service.BetaAvailability);
        var latency = BetaScore(service.AlphaLatency, service.BetaLatency);
        var conformity = BetaScore(service.AlphaConformity, service.BetaConformity);

        var globalScore = AvailabilityWeight * availability
                        + LatencyWeight * latency
                        + ConformityWeight * conformity;

        // Confidence reflects how much evidence backs the score, not the variance of the Beta(1,1)
        // prior (which is a near-constant 0.9167 even with zero ratings — an unknown service must
        // not look near-certain). Use the effective number of observations accumulated beyond the
        // (1,1) prior, smoothed so confidence is 0 with no data and rises toward 1 with more.
        var effectiveObservations = Math.Max(0.0, service.Alpha + service.Beta - 2.0);
        var confidence = effectiveObservations / (effectiveObservations + ConfidenceSmoothing);

        return new ServiceScore
        {
            ServiceDid = service.Did,
            Score = Math.Round(globalScore, 4),
            Confidence = Math.Round(Math.Max(0, confidence), 4),
            RatingsCount = service.RatingsCount,
            Dimensions = new DimensionScores
            {
                Availability = Math.Round(availability, 4),
                Latency = Math.Round(latency, 4),
                Conformity = Math.Round(conformity, 4),
            },
            RecentIncidents = null, // not tracked yet; null rather than a 0 that reads as "none"
            LastRatedAt = service.LastRatedAt,
            ServiceSupportsReceipts = service.SupportsReceipts,
        };
    }

    public ServiceScore CalculateProviderScore(string provider, IReadOnlyList<ServiceEntity> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return new ServiceScore
            {
                ServiceDid = provider,
                Score = 0.5,
                Confidence = 0.0,
                RatingsCount = 0,
                Dimensions = new DimensionScores { Availability = 0.5, Latency = 0.5, Conformity = 0.5 },
                ServiceSupportsReceipts = false,
            };
        }

        // Weighted by the evidence each endpoint holds (its effective observations, which already
        // reflect rating weight and age), not by its raw number of ratings: a flood of near-weightless
        // ratings on one path must not drown the endpoints that carry real evidence.
        var totalRatings = endpoints.Sum(e => e.RatingsCount);
        if (totalRatings == 0) totalRatings = 1;
        var totalEvidence = endpoints.Sum(Evidence);

        double avgScore = 0, avgAvail = 0, avgLatency = 0, avgConf = 0, avgConfidence = 0;
        var supportsReceipts = false;
        DateTimeOffset? lastRated = null;

        foreach (var ep in endpoints)
        {
            var w = totalEvidence > 0 ? Evidence(ep) / totalEvidence : (double)ep.RatingsCount / totalRatings;
            var score = CalculateScore(ep);
            avgScore += score.Score * w;
            avgAvail += score.Dimensions.Availability * w;
            avgLatency += score.Dimensions.Latency * w;
            avgConf += score.Dimensions.Conformity * w;
            avgConfidence += score.Confidence * w;
            if (score.ServiceSupportsReceipts) supportsReceipts = true;
            if (lastRated is null || score.LastRatedAt > lastRated) lastRated = score.LastRatedAt;
        }

        return new ServiceScore
        {
            ServiceDid = provider,
            Score = Math.Round(avgScore, 4),
            Confidence = Math.Round(avgConfidence, 4),
            RatingsCount = totalRatings,
            Dimensions = new DimensionScores
            {
                Availability = Math.Round(avgAvail, 4),
                Latency = Math.Round(avgLatency, 4),
                Conformity = Math.Round(avgConf, 4),
            },
            ServiceSupportsReceipts = supportsReceipts,
            LastRatedAt = lastRated,
        };
    }

    /// <summary>
    /// The factor existing evidence is multiplied by after <paramref name="elapsed"/>: 0.5 per
    /// half-life. ServiceRepository applies the same formula in SQL.
    /// </summary>
    public static double DecayFactor(TimeSpan elapsed, double halfLifeDays) =>
        elapsed <= TimeSpan.Zero ? 1.0 : Math.Pow(0.5, elapsed.TotalDays / halfLifeDays);

    public ServiceEntity ApplyRating(ServiceEntity service, Rating rating) =>
        ApplyRating(service, rating, DateTimeOffset.UtcNow);

    public ServiceEntity ApplyRating(ServiceEntity service, Rating rating, DateTimeOffset now)
    {
        var weight = rating.Weight;
        var metrics = rating.Metrics;

        // Forget in proportion to the time since evidence was last added.
        var decay = service.LastRatedAt is { } last ? DecayFactor(now - last, _halfLifeDays) : 1.0;
        service.Alpha *= decay;
        service.Beta *= decay;
        service.AlphaAvailability *= decay;
        service.BetaAvailability *= decay;
        service.AlphaLatency *= decay;
        service.BetaLatency *= decay;
        service.AlphaConformity *= decay;
        service.BetaConformity *= decay;

        // Availability: 2xx (or a valid x402 challenge) = positive, 5xx = negative, others = neutral
        if (ResponseOutcome.IsSuccess(metrics.StatusCode, metrics.SchemaValid))
        {
            service.AlphaAvailability += weight;
            service.Alpha += weight;
        }
        else if (ResponseOutcome.IsFailure(metrics.StatusCode))
        {
            service.BetaAvailability += weight;
            service.Beta += weight;
        }

        // Latency: below threshold = positive, above = negative
        if (metrics.LatencyMs > 0 && metrics.LatencyMs <= _latencyThresholdMs)
        {
            service.AlphaLatency += weight;
            service.Alpha += weight;
        }
        else if (metrics.LatencyMs > _latencyThresholdMs)
        {
            service.BetaLatency += weight;
            service.Beta += weight;
        }

        // Conformity: schema valid = positive, invalid = negative
        if (metrics.SchemaValid == true)
        {
            service.AlphaConformity += weight;
            service.Alpha += weight;
        }
        else if (metrics.SchemaValid == false)
        {
            service.BetaConformity += weight;
            service.Beta += weight;
        }

        // Quality score modulation (optional, 1-5 scale)
        if (rating.QualityScore.HasValue)
        {
            var qualityFactor = (rating.QualityScore.Value - 3.0) / 2.0; // -1.0 to +1.0
            var qualityWeight = weight * 0.25; // quality counts for 25% max
            if (qualityFactor > 0)
                service.Alpha += qualityWeight * qualityFactor;
            else
                service.Beta += qualityWeight * Math.Abs(qualityFactor);
        }

        // Same floor as the SQL update, so this stays a faithful model of what is stored.
        service.Alpha = Math.Max(1.0, service.Alpha);
        service.Beta = Math.Max(1.0, service.Beta);
        service.AlphaAvailability = Math.Max(1.0, service.AlphaAvailability);
        service.BetaAvailability = Math.Max(1.0, service.BetaAvailability);
        service.AlphaLatency = Math.Max(1.0, service.AlphaLatency);
        service.BetaLatency = Math.Max(1.0, service.BetaLatency);
        service.AlphaConformity = Math.Max(1.0, service.AlphaConformity);
        service.BetaConformity = Math.Max(1.0, service.BetaConformity);

        service.RatingsCount++;
        service.LastRatedAt = now;
        service.UpdatedAt = now;

        if (rating.ReceiptVerified)
            service.SupportsReceipts = true;

        return service;
    }

    public RatingDelta ComputeDelta(Rating rating)
    {
        var weight = rating.Weight;
        var metrics = rating.Metrics;
        var delta = new RatingDeltaBuilder();

        // Availability
        if (ResponseOutcome.IsSuccess(metrics.StatusCode, metrics.SchemaValid))
        {
            delta.AlphaAvailability += weight;
            delta.Alpha += weight;
        }
        else if (ResponseOutcome.IsFailure(metrics.StatusCode))
        {
            delta.BetaAvailability += weight;
            delta.Beta += weight;
        }

        // Latency
        if (metrics.LatencyMs > 0 && metrics.LatencyMs <= _latencyThresholdMs)
        {
            delta.AlphaLatency += weight;
            delta.Alpha += weight;
        }
        else if (metrics.LatencyMs > _latencyThresholdMs)
        {
            delta.BetaLatency += weight;
            delta.Beta += weight;
        }

        // Conformity
        if (metrics.SchemaValid == true)
        {
            delta.AlphaConformity += weight;
            delta.Alpha += weight;
        }
        else if (metrics.SchemaValid == false)
        {
            delta.BetaConformity += weight;
            delta.Beta += weight;
        }

        // Quality score modulation
        if (rating.QualityScore.HasValue)
        {
            var qualityFactor = (rating.QualityScore.Value - 3.0) / 2.0;
            var qualityWeight = weight * 0.25;
            if (qualityFactor > 0)
                delta.Alpha += qualityWeight * qualityFactor;
            else
                delta.Beta += qualityWeight * Math.Abs(qualityFactor);
        }

        return new RatingDelta
        {
            AlphaDelta = delta.Alpha,
            BetaDelta = delta.Beta,
            AlphaAvailabilityDelta = delta.AlphaAvailability,
            BetaAvailabilityDelta = delta.BetaAvailability,
            AlphaLatencyDelta = delta.AlphaLatency,
            BetaLatencyDelta = delta.BetaLatency,
            AlphaConformityDelta = delta.AlphaConformity,
            BetaConformityDelta = delta.BetaConformity,
            SupportsReceipts = rating.ReceiptVerified,
        };
    }

    private static double Evidence(ServiceEntity e) => Math.Max(0.0, e.Alpha + e.Beta - 2.0);

    private static double BetaScore(double alpha, double beta)
    {
        var denom = alpha + beta;
        return denom > 0 ? alpha / denom : 0.5;
    }

    private class RatingDeltaBuilder
    {
        public double Alpha;
        public double Beta;
        public double AlphaAvailability;
        public double BetaAvailability;
        public double AlphaLatency;
        public double BetaLatency;
        public double AlphaConformity;
        public double BetaConformity;
    }
}

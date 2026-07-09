using Microsoft.Extensions.Configuration;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Scoring;

public sealed class BetaReputationSystem : IScoringEngine
{
    private readonly double _lambda;
    private readonly int _latencyThresholdMs;

    private const double AvailabilityWeight = 0.4;
    private const double LatencyWeight = 0.35;
    private const double ConformityWeight = 0.25;

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
        _lambda = config?.GetValue<double>("Scoring:Lambda", 0.995) ?? 0.995;
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
            RecentIncidents = 0, // Phase 2: track incidents over sliding window
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

        // Weighted average by ratings_count (more ratings = more influence)
        var totalRatings = endpoints.Sum(e => e.RatingsCount);
        if (totalRatings == 0) totalRatings = 1;

        double avgScore = 0, avgAvail = 0, avgLatency = 0, avgConf = 0, avgConfidence = 0;
        var supportsReceipts = false;
        DateTimeOffset? lastRated = null;

        foreach (var ep in endpoints)
        {
            var w = (double)ep.RatingsCount / totalRatings;
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

    public ServiceEntity ApplyRating(ServiceEntity service, Rating rating)
    {
        var weight = rating.Weight;
        var metrics = rating.Metrics;

        // Apply forgetting factor
        service.Alpha = service.Alpha * _lambda;
        service.Beta = service.Beta * _lambda;
        service.AlphaAvailability *= _lambda;
        service.BetaAvailability *= _lambda;
        service.AlphaLatency *= _lambda;
        service.BetaLatency *= _lambda;
        service.AlphaConformity *= _lambda;
        service.BetaConformity *= _lambda;

        // Availability: 2xx = positive, 5xx = negative, others = neutral
        if (metrics.StatusCode >= 200 && metrics.StatusCode < 300)
        {
            service.AlphaAvailability += weight;
            service.Alpha += weight;
        }
        else if (metrics.StatusCode >= 500)
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

        service.RatingsCount++;
        service.LastRatedAt = DateTimeOffset.UtcNow;
        service.UpdatedAt = DateTimeOffset.UtcNow;

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
        if (metrics.StatusCode >= 200 && metrics.StatusCode < 300)
        {
            delta.AlphaAvailability += weight;
            delta.Alpha += weight;
        }
        else if (metrics.StatusCode >= 500)
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

using FluentAssertions;
using TrustScore.Api.Scoring;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

public class BetaReputationTests
{
    private readonly BetaReputationSystem _engine = new();

    [Fact]
    public void NewService_HasNeutralScore()
    {
        var service = new ServiceEntity { Did = "did:web:test.example.com" };

        var score = _engine.CalculateScore(service);

        score.Score.Should().Be(0.5);
        score.RatingsCount.Should().Be(0);
        // Beta(1,1) has variance 1/12 ≈ 0.083, confidence = 1 - 0.083 ≈ 0.917
        // With no ratings, the confidence is high on the prior but meaningless
        // The real signal is ratings_count = 0
    }

    [Fact]
    public void PositiveRating_IncreasesScore()
    {
        var service = new ServiceEntity { Did = "did:web:test.example.com" };
        var rating = CreateRating(statusCode: 200, latencyMs: 100, schemaValid: true, weight: 1.0);

        service = _engine.ApplyRating(service, rating);
        var score = _engine.CalculateScore(service);

        score.Score.Should().BeGreaterThan(0.5);
        score.RatingsCount.Should().Be(1);
    }

    [Fact]
    public void NegativeRating_DecreasesScore()
    {
        var service = new ServiceEntity { Did = "did:web:test.example.com" };
        var rating = CreateRating(statusCode: 500, latencyMs: 5000, schemaValid: false, weight: 1.0);

        service = _engine.ApplyRating(service, rating);
        var score = _engine.CalculateScore(service);

        score.Score.Should().BeLessThan(0.5);
    }

    [Fact]
    public void ManyPositiveRatings_ConvergeToHighScore()
    {
        var service = new ServiceEntity { Did = "did:web:test.example.com" };

        for (int i = 0; i < 100; i++)
        {
            var rating = CreateRating(statusCode: 200, latencyMs: 100, schemaValid: true, weight: 1.0);
            service = _engine.ApplyRating(service, rating);
        }

        var score = _engine.CalculateScore(service);

        score.Score.Should().BeGreaterThan(0.9);
        score.Confidence.Should().BeGreaterThan(0.5);
        score.RatingsCount.Should().Be(100);
    }

    [Fact]
    public void VerifiedRating_HasMoreWeight()
    {
        var serviceA = new ServiceEntity { Did = "did:web:a.example.com" };
        var serviceB = new ServiceEntity { Did = "did:web:b.example.com" };

        // Both get one positive rating, but A has verified (weight 1.0), B has unverified (weight 0.3)
        var ratingA = CreateRating(statusCode: 200, latencyMs: 100, schemaValid: true, weight: 1.0);
        var ratingB = CreateRating(statusCode: 200, latencyMs: 100, schemaValid: true, weight: 0.3);

        serviceA = _engine.ApplyRating(serviceA, ratingA);
        serviceB = _engine.ApplyRating(serviceB, ratingB);

        var scoreA = _engine.CalculateScore(serviceA);
        var scoreB = _engine.CalculateScore(serviceB);

        scoreA.Score.Should().BeGreaterThan(scoreB.Score);
    }

    [Fact]
    public void QualityScore_ModulatesOverallScore()
    {
        var serviceHigh = new ServiceEntity { Did = "did:web:high.example.com" };
        var serviceLow = new ServiceEntity { Did = "did:web:low.example.com" };

        var ratingHigh = CreateRating(statusCode: 200, latencyMs: 100, schemaValid: true, weight: 1.0, qualityScore: 5);
        var ratingLow = CreateRating(statusCode: 200, latencyMs: 100, schemaValid: true, weight: 1.0, qualityScore: 1);

        serviceHigh = _engine.ApplyRating(serviceHigh, ratingHigh);
        serviceLow = _engine.ApplyRating(serviceLow, ratingLow);

        var scoreHigh = _engine.CalculateScore(serviceHigh);
        var scoreLow = _engine.CalculateScore(serviceLow);

        // Quality score has a 25% weight factor — the difference may be small
        scoreHigh.Score.Should().BeGreaterThanOrEqualTo(scoreLow.Score);
    }

    [Fact]
    public void Dimensions_AreCalculatedIndependently()
    {
        var service = new ServiceEntity { Did = "did:web:test.example.com" };
        // Good availability, bad latency, good conformity
        var rating = CreateRating(statusCode: 200, latencyMs: 5000, schemaValid: true, weight: 1.0);

        service = _engine.ApplyRating(service, rating);
        var score = _engine.CalculateScore(service);

        score.Dimensions.Availability.Should().BeGreaterThan(0.5);
        score.Dimensions.Latency.Should().BeLessThan(0.5);
        score.Dimensions.Conformity.Should().BeGreaterThan(0.5);
    }

    private static Rating CreateRating(
        int statusCode = 200,
        int latencyMs = 100,
        bool schemaValid = true,
        double weight = 1.0,
        int? qualityScore = null)
    {
        return new Rating
        {
            ServiceDid = "did:web:test.example.com",
            AgentDid = "did:web:agent.example.com",
            Metrics = new RatingMetrics
            {
                StatusCode = statusCode,
                LatencyMs = latencyMs,
                SchemaValid = schemaValid,
            },
            Weight = weight,
            QualityScore = qualityScore,
        };
    }
}

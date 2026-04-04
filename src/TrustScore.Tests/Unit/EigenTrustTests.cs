using FluentAssertions;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using Xunit;

namespace TrustScore.Tests.Unit;

public class EigenTrustTests
{
    private readonly EigenTrustEngine _engine = new();

    [Fact]
    public void EmptyRatings_ReturnsEmpty()
    {
        var result = _engine.ComputeTrustScores(new List<AgentRatingRecord>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void SingleAgent_GetsDefaultTrust()
    {
        var ratings = new List<AgentRatingRecord>
        {
            new("agent-a", "service-1", 200, 100, true, false),
        };

        var result = _engine.ComputeTrustScores(ratings);

        result.Should().ContainKey("agent-a");
        result["agent-a"].Should().Be(0.5);
    }

    [Fact]
    public void ConsistentAgent_GetsHigherTrust()
    {
        // 3 honest agents rate consistently (200 status, low latency)
        // 1 dishonest agent rates inconsistently (claims 500 on same services)
        // The 3 honest agents form the consensus majority
        var ratings = new List<AgentRatingRecord>();

        // 3 honest agents rate 10 services consistently
        for (int h = 0; h < 3; h++)
            for (int i = 0; i < 10; i++)
                ratings.Add(new($"honest-{h}", $"service-{i}", 200, 100, true, true));

        // 1 dishonest agent rates same services inconsistently
        for (int i = 0; i < 10; i++)
            ratings.Add(new("dishonest", $"service-{i}", 500, 5000, false, false));

        var result = _engine.ComputeTrustScores(ratings);

        var honestAvg = Enumerable.Range(0, 3).Average(h => result[$"honest-{h}"]);
        result["dishonest"].Should().BeLessThan(honestAvg,
            "dishonest agent should have lower trust than honest majority");
    }

    [Fact]
    public void VerifiedRater_BecomeSeedRater()
    {
        var ratings = new List<AgentRatingRecord>();

        // Agent A has verified ratings (receipts)
        for (int i = 0; i < 10; i++)
            ratings.Add(new("agent-a", $"service-{i}", 200, 100, true, true));

        // Agent B has unverified ratings
        for (int i = 0; i < 10; i++)
            ratings.Add(new("agent-b", $"service-{i}", 200, 100, true, false));

        var result = _engine.ComputeTrustScores(ratings);

        result["agent-a"].Should().BeGreaterThanOrEqualTo(result["agent-b"],
            "verified rater (seed) should have at least as much trust");
    }

    [Fact]
    public void SybilCluster_GetsLowTrust()
    {
        var ratings = new List<AgentRatingRecord>();

        // 3 honest agents rating 10 services consistently
        for (int h = 0; h < 3; h++)
            for (int i = 0; i < 10; i++)
                ratings.Add(new($"honest-{h}", $"service-{i}", 200, 100, true, true));

        // 10 Sybil agents all rating 1 specific service with fake high scores
        // (claiming 200 when consensus says otherwise)
        // They only rate "sybil-target" and nothing else
        for (int s = 0; s < 10; s++)
            ratings.Add(new($"sybil-{s}", "sybil-target", 200, 50, true, false));

        var result = _engine.ComputeTrustScores(ratings);

        // Honest agents should have higher trust than Sybil agents
        var honestAvg = Enumerable.Range(0, 3).Average(h => result[$"honest-{h}"]);
        var sybilAvg = Enumerable.Range(0, 10).Average(s => result[$"sybil-{s}"]);

        honestAvg.Should().BeGreaterThan(sybilAvg,
            "honest agents with diverse verified ratings should outrank Sybil cluster");
    }

    [Fact]
    public void AllScores_AreWithinBounds()
    {
        var ratings = new List<AgentRatingRecord>();
        for (int a = 0; a < 20; a++)
            for (int s = 0; s < 5; s++)
                ratings.Add(new($"agent-{a}", $"service-{s}", 200, 100 + a * 10, true, a < 5));

        var result = _engine.ComputeTrustScores(ratings);

        foreach (var (_, score) in result)
        {
            score.Should().BeGreaterThanOrEqualTo(0.1, "minimum trust is 0.1");
            score.Should().BeLessThanOrEqualTo(1.0, "maximum trust is 1.0");
        }
    }

    [Fact]
    public void ManyAgents_Converges()
    {
        var ratings = new List<AgentRatingRecord>();

        // 100 agents, 50 services
        for (int a = 0; a < 100; a++)
            for (int s = 0; s < 50; s++)
                ratings.Add(new($"agent-{a}", $"service-{s}", 200, 100 + a, true, a < 10));

        var result = _engine.ComputeTrustScores(ratings);

        result.Should().HaveCount(100);
        // Should complete without timeout or exception
    }
}

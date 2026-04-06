using FluentAssertions;
using TrustScore.Api.Scoring;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// Tests for the components used by the hourly job.
/// The job itself orchestrates EigenTrust + Merkle — we test them separately.
/// </summary>
public class HourlyJobComponentTests
{
    [Fact]
    public void EigenTrust_ProducesScores_FromRatings()
    {
        var engine = new EigenTrustEngine();
        var ratings = new List<AgentRatingRecord>
        {
            new("agent-a", "service-1", 200, 100, true, true),
            new("agent-a", "service-2", 200, 150, true, false),
            new("agent-b", "service-1", 200, 100, true, false),
            new("agent-b", "service-2", 500, 5000, false, false),
        };

        var scores = engine.ComputeTrustScores(ratings);

        scores.Should().HaveCount(2);
        scores.Should().ContainKey("agent-a");
        scores.Should().ContainKey("agent-b");
        scores.Values.Should().AllSatisfy(s =>
        {
            s.Should().BeGreaterThanOrEqualTo(0.1);
            s.Should().BeLessThanOrEqualTo(1.0);
        });
    }

    [Fact]
    public void MerkleTree_BuildsFromLeaves_AndProducesRoot()
    {
        var tree = new MerkleTree();
        var leaves = new[]
        {
            (Guid.NewGuid(), "service-1.example.com", DateTimeOffset.UtcNow),
            (Guid.NewGuid(), "service-2.example.com", DateTimeOffset.UtcNow),
            (Guid.NewGuid(), "service-1.example.com", DateTimeOffset.UtcNow),
        };

        foreach (var (id, did, ts) in leaves)
        {
            var hash = MerkleTree.ComputeLeafHash(id, did, ts);
            tree.AddLeafHash(hash);
        }

        tree.LeafCount.Should().Be(3);
        tree.RootHex.Should().NotBeNullOrEmpty();
        tree.RootHex!.Length.Should().Be(64); // SHA256 = 32 bytes = 64 hex chars
    }
}

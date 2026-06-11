using System.Data;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using TrustScore.Api.Data;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// Exercises the real AuditService inclusion-proof path (snapshot build + cache + anchor-root
/// guard) without a database, by calling the internal BuildInclusionProofAsync directly. This
/// covers the core audit guarantee: a returned proof verifies against the anchored root.
/// </summary>
public class AuditServiceTests
{
    private static AuditService NewService(IRatingRepository repo) =>
        new(new DbConnectionFactory("Host=unused;Database=unused;"), repo, new MemoryCache(new MemoryCacheOptions()));

    private static List<RatingLeafInfo> BuildLeaves(int count)
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var leaves = new List<RatingLeafInfo>();
        for (int i = 0; i < count; i++)
        {
            var id = new Guid($"00000000-0000-0000-0000-0000000000{i:D2}");
            var created = baseTime.AddMinutes(i); // distinct, already in (created_at, id) order
            var hashHex = Convert.ToHexString(
                MerkleTree.ComputeLeafHash(id, "api.example.com", created)).ToLowerInvariant();
            leaves.Add(new RatingLeafInfo(id, "api.example.com", created, hashHex));
        }
        return leaves;
    }

    private static string AnchoredRoot(IReadOnlyList<RatingLeafInfo> leaves)
    {
        var tree = new MerkleTree();
        foreach (var leaf in leaves)
            tree.AddLeafHash(MerkleTree.ComputeLeafHash(leaf.Id, leaf.ServiceDid, leaf.CreatedAt));
        return tree.RootHex!;
    }

    [Fact]
    public async Task Proof_VerifiesAgainstAnchoredRoot()
    {
        var leaves = BuildLeaves(5);
        var root = AnchoredRoot(leaves);
        var anchor = new MerkleAnchor { Id = 1, MerkleRoot = root, LeafCount = leaves.Count };
        var target = leaves[2];
        var service = NewService(new StubRatingRepo(leaves));

        var result = await service.BuildInclusionProofAsync(anchor, target, target.Id);

        result.Should().NotBeNull();
        result!.MerkleRoot.Should().Be(root);
        result.LeafIndex.Should().Be(2);
        result.TotalLeaves.Should().Be(5);

        // The decisive check: the returned proof must reconstruct the anchored root.
        var leafHash = Convert.FromHexString(result.LeafHash);
        var proof = result.Proof.Select(p => new ProofNode(Convert.FromHexString(p.Hash), p.IsRight)).ToList();
        MerkleTree.VerifyProof(leafHash, proof, Convert.FromHexString(root)).Should().BeTrue();
    }

    [Fact]
    public async Task Proof_ForRatingNotInAnchoredSet_ReturnsNull()
    {
        var leaves = BuildLeaves(4);
        var anchor = new MerkleAnchor { Id = 1, MerkleRoot = AnchoredRoot(leaves), LeafCount = leaves.Count };

        // A rating that exists (has a leaf) but is not part of the anchored snapshot.
        var newer = new RatingLeafInfo(
            new Guid("00000000-0000-0000-0000-0000000000ff"), "api.example.com",
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), "deadbeef");
        var service = NewService(new StubRatingRepo(leaves));

        var result = await service.BuildInclusionProofAsync(anchor, newer, newer.Id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Proof_WhenSnapshotRootDoesNotMatchAnchor_ReturnsNull()
    {
        var leaves = BuildLeaves(5);
        // Anchor claims a different root than the leaves actually produce → drift guard kicks in.
        var anchor = new MerkleAnchor { Id = 1, MerkleRoot = new string('a', 64), LeafCount = leaves.Count };
        var target = leaves[1];
        var service = NewService(new StubRatingRepo(leaves));

        var result = await service.BuildInclusionProofAsync(anchor, target, target.Id);

        result.Should().BeNull();
    }

    private sealed class StubRatingRepo : IRatingRepository
    {
        private readonly IReadOnlyList<RatingLeafInfo> _leaves;
        public StubRatingRepo(IReadOnlyList<RatingLeafInfo> leaves) => _leaves = leaves;

        public Task<IReadOnlyList<RatingLeafInfo>> GetAnchoredLeafHashesAsync(int leafCount)
            => Task.FromResult<IReadOnlyList<RatingLeafInfo>>(_leaves.Take(leafCount).ToList());

        // Unused by BuildInclusionProofAsync.
        public Task InsertAsync(Rating rating) => throw new NotImplementedException();
        public Task InsertAsync(IDbConnection conn, IDbTransaction tx, Rating rating) => throw new NotImplementedException();
        public Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window) => throw new NotImplementedException();
        public Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId) => throw new NotImplementedException();
        public Task<IReadOnlyList<RatingLeafInfo>> GetAllLeafHashesAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<RatingSummary>> GetHistoryAsync(string serviceDid, int months) => throw new NotImplementedException();
        public Task<IReadOnlyList<DailyHistoryPoint>> GetDailyHistoryAsync(string serviceDid, int months) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentRatingRecord>> GetAllRatingsForTrustAsync() => throw new NotImplementedException();
    }
}

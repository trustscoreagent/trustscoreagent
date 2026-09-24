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

    private static StoredLeaf Store(RatingLeaf leaf) => new(
        leaf.Id, leaf.ServiceDid, leaf.CreatedAt, leaf.LeafVersion, leaf.StatusCode, leaf.LatencyMs,
        leaf.ResponseSizeBytes, leaf.SchemaValid, leaf.QualityScore, leaf.HasReceipt,
        leaf.ReceiptVerified, leaf.SignatureVerified, leaf.Weight, leaf.HashHex());

    // Leaves in (created_at, id) order; the first `v1Count` use the legacy format, as the rows
    // written before v2 do.
    private static List<StoredLeaf> BuildLeaves(int count, int v1Count = 0)
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var leaves = new List<StoredLeaf>();
        for (int i = 0; i < count; i++)
        {
            var leaf = new RatingLeaf(
                new Guid($"00000000-0000-0000-0000-0000000000{i:D2}"), "api.example.com",
                baseTime.AddMinutes(i), i < v1Count ? 1 : 2,
                200, 100 + i, null, true, 4, false, false, i % 2 == 0, 0.3);
            leaves.Add(Store(leaf));
        }
        return leaves;
    }

    private static string AnchoredRoot(IReadOnlyList<StoredLeaf> leaves, MerkleTreeVersion version)
    {
        var tree = new MerkleTree(version);
        foreach (var leaf in leaves)
            tree.AddLeafHash(leaf.AnchoredHash());
        return tree.RootHex!;
    }

    private static MerkleAnchor Anchor(IReadOnlyList<StoredLeaf> leaves, MerkleTreeVersion version) => new()
    {
        Id = 1,
        MerkleRoot = AnchoredRoot(leaves, version),
        LeafCount = leaves.Count,
        TreeVersion = (int)version,
    };

    private static RatingLeafInfo Info(StoredLeaf leaf) =>
        new(leaf.Id, leaf.ServiceDid, leaf.CreatedAt, leaf.MerkleLeafHash);

    private static bool Verifies(InclusionProofResult result)
    {
        var proof = result.Proof.Select(p => new ProofNode(Convert.FromHexString(p.Hash), p.IsRight)).ToList();
        return MerkleTree.VerifyProof(
            Convert.FromHexString(result.LeafHash), proof, Convert.FromHexString(result.MerkleRoot), result.TreeVersion);
    }

    [Theory]
    [InlineData(MerkleTreeVersion.V1)]
    [InlineData(MerkleTreeVersion.V2)]
    public async Task Proof_VerifiesAgainstAnchoredRoot_UnderTheAnchorsVersion(MerkleTreeVersion version)
    {
        var leaves = BuildLeaves(5);
        var anchor = Anchor(leaves, version);
        var service = NewService(new StubRatingRepo(leaves));

        var result = await service.BuildInclusionProofAsync(anchor, Info(leaves[2]), leaves[2].Id);

        result.Should().NotBeNull();
        result!.MerkleRoot.Should().Be(anchor.MerkleRoot);
        result.TreeVersion.Should().Be(version);
        result.LeafIndex.Should().Be(2);
        result.TotalLeaves.Should().Be(5);
        Verifies(result).Should().BeTrue();
    }

    [Fact]
    public async Task MixedTree_ProvesLegacyAndV2Leaves()
    {
        // The first v2 anchor still contains every earlier v1 leaf.
        var leaves = BuildLeaves(7, v1Count: 4);
        var anchor = Anchor(leaves, MerkleTreeVersion.V2);
        var service = NewService(new StubRatingRepo(leaves));

        foreach (var i in new[] { 0, 3, 4, 6 })
        {
            var result = await service.BuildInclusionProofAsync(anchor, Info(leaves[i]), leaves[i].Id);
            Verifies(result!).Should().BeTrue($"leaf {i}");
            result!.Leaf.LeafVersion.Should().Be(i < 4 ? 1 : 2);
            result.Leaf.HashHex().Should().Be(result.LeafHash, "the returned fields recompute the leaf");
        }
    }

    [Fact]
    public async Task EditedRow_KeepsItsProof_ButItsFieldsNoLongerMatchTheLeaf()
    {
        // Someone rewrites a rating's status after it was anchored. The anchored root still stands
        // (the tree uses the hash stored at insertion), and the proof exposes the edit instead of
        // silently certifying the new value.
        var leaves = BuildLeaves(5);
        var anchor = Anchor(leaves, MerkleTreeVersion.V2);
        leaves[2] = leaves[2] with { StatusCode = 503 };
        var service = NewService(new StubRatingRepo(leaves));

        var result = await service.BuildInclusionProofAsync(anchor, Info(leaves[2]), leaves[2].Id);

        Verifies(result!).Should().BeTrue();
        result!.Leaf.StatusCode.Should().Be(503);
        result.Leaf.HashHex().Should().NotBe(result.LeafHash);
        leaves[2].IsIntact().Should().BeFalse();
    }

    [Fact]
    public async Task StaleV1StoredHash_DoesNotChangeTheRoot()
    {
        // Migration 004 rewrote service ids of early ratings after their v1 hash was stored. Every
        // v1 anchor recomputed v1 leaves, so the stored value must not be what goes in the tree.
        var leaves = BuildLeaves(5, v1Count: 3);
        var anchor = Anchor(leaves, MerkleTreeVersion.V2);
        leaves[1] = leaves[1] with { MerkleLeafHash = new string('0', 64) };
        var service = NewService(new StubRatingRepo(leaves));

        var result = await service.BuildInclusionProofAsync(anchor, Info(leaves[1]), leaves[1].Id);

        Verifies(result!).Should().BeTrue();
        leaves[1].IsIntact().Should().BeTrue("v1 leaves have no stored hash to be faithful to");
    }

    [Fact]
    public async Task Proof_ForRatingNotInAnchoredSet_ReturnsNull()
    {
        var leaves = BuildLeaves(4);
        var anchor = Anchor(leaves, MerkleTreeVersion.V2);
        var newer = new RatingLeafInfo(
            new Guid("00000000-0000-0000-0000-0000000000ff"), "api.example.com",
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), "deadbeef");
        var service = NewService(new StubRatingRepo(leaves));

        (await service.BuildInclusionProofAsync(anchor, newer, newer.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Proof_WithCutoff_ReproducesFromCutoffAndExcludesLaterLeaves()
    {
        // 5 leaves exist in the repo, but the anchor was taken at a cutoff covering only the first
        // 4. The snapshot must reproduce from the cutoff, a pre-cutoff rating must be provable, and
        // the post-cutoff one must not be, even though it is present in the table.
        var all = BuildLeaves(5);
        var anchored = all.Take(4).ToList();
        var anchor = new MerkleAnchor
        {
            Id = 1,
            MerkleRoot = AnchoredRoot(anchored, MerkleTreeVersion.V2),
            LeafCount = anchored.Count,
            CutoffAt = anchored[^1].CreatedAt,
            TreeVersion = 2,
        };
        var service = NewService(new StubRatingRepo(all));

        var provable = await service.BuildInclusionProofAsync(anchor, Info(anchored[1]), anchored[1].Id);
        provable.Should().NotBeNull();
        provable!.TotalLeaves.Should().Be(4);

        (await service.BuildInclusionProofAsync(anchor, Info(all[4]), all[4].Id)).Should().BeNull();
    }

    [Fact]
    public async Task Proof_WhenSnapshotRootDoesNotMatchAnchor_ReturnsNull()
    {
        var leaves = BuildLeaves(5);
        var anchor = new MerkleAnchor { Id = 1, MerkleRoot = new string('a', 64), LeafCount = leaves.Count, TreeVersion = 2 };
        var service = NewService(new StubRatingRepo(leaves));

        (await service.BuildInclusionProofAsync(anchor, Info(leaves[1]), leaves[1].Id)).Should().BeNull();
    }

    [Fact]
    public async Task Proof_UnderTheWrongVersion_ReturnsNull()
    {
        // A v1 root rebuilt as v2 (or the reverse) does not match, so no proof is handed out.
        var leaves = BuildLeaves(5);
        var anchor = Anchor(leaves, MerkleTreeVersion.V1);
        var mislabelled = new MerkleAnchor
        {
            Id = 1,
            MerkleRoot = anchor.MerkleRoot,
            LeafCount = anchor.LeafCount,
            TreeVersion = 2,
        };
        var service = NewService(new StubRatingRepo(leaves));

        (await service.BuildInclusionProofAsync(mislabelled, Info(leaves[1]), leaves[1].Id)).Should().BeNull();
    }

    private sealed class StubRatingRepo : IRatingRepository
    {
        private readonly IReadOnlyList<StoredLeaf> _leaves;
        public StubRatingRepo(IReadOnlyList<StoredLeaf> leaves) => _leaves = leaves;

        public Task<IReadOnlyList<StoredLeaf>> GetFirstLeavesAsync(int leafCount)
            => Task.FromResult<IReadOnlyList<StoredLeaf>>(_leaves.Take(leafCount).ToList());

        public Task<IReadOnlyList<StoredLeaf>> GetLeavesUpToAsync(DateTimeOffset cutoff)
            => Task.FromResult<IReadOnlyList<StoredLeaf>>(
                _leaves.Where(l => l.CreatedAt <= cutoff).OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToList());

        // Unused by BuildInclusionProofAsync.
        public Task InsertAsync(Rating rating) => throw new NotImplementedException();
        public Task InsertAsync(IDbConnection conn, IDbTransaction tx, Rating rating) => throw new NotImplementedException();
        public Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window) => throw new NotImplementedException();
        public Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId) => throw new NotImplementedException();
        public Task<IReadOnlyList<RatingSummary>> GetHistoryAsync(string serviceDid, int months) => throw new NotImplementedException();
        public Task<IReadOnlyList<DailyHistoryPoint>> GetDailyHistoryAsync(string serviceDid, int months) => throw new NotImplementedException();
        public Task<IReadOnlyList<AgentRatingRecord>> GetAllRatingsForTrustAsync() => throw new NotImplementedException();
    }
}

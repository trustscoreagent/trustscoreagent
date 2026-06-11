using Dapper;
using Microsoft.Extensions.Caching.Memory;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class AuditService : IAuditService
{
    private readonly DbConnectionFactory _db;
    private readonly IRatingRepository _ratingRepo;
    private readonly IMemoryCache _memoryCache;

    public AuditService(DbConnectionFactory db, IRatingRepository ratingRepo, IMemoryCache memoryCache)
    {
        _db = db;
        _ratingRepo = ratingRepo;
        _memoryCache = memoryCache;
    }

    private sealed record MerkleSnapshot(MerkleTree Tree, IReadOnlyDictionary<Guid, int> Index);

    public async Task<MerkleAnchor?> GetLatestAnchorAsync()
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<MerkleAnchor>(
            """
            SELECT id AS Id,
                   merkle_root AS MerkleRoot,
                   leaf_count AS LeafCount,
                   anchored_at AS AnchoredAt,
                   blockchain AS Blockchain,
                   contract_address AS ContractAddress,
                   transaction_hash AS TransactionHash,
                   block_number AS BlockNumber
            FROM merkle_anchors
            ORDER BY anchored_at DESC
            LIMIT 1
            """);
    }

    public async Task<InclusionProofResult?> GetInclusionProofAsync(Guid ratingId)
    {
        // A proof is only meaningful against a *published* (anchored) root. Rebuilding from the
        // live table would yield a root that drifts with every new rating and never matches
        // /v1/audit/root, defeating the audit guarantee. So we rebuild the exact snapshot the
        // latest anchor committed to and prove against that anchor's root.
        var anchor = await GetLatestAnchorAsync();
        if (anchor is null || anchor.LeafCount == 0)
            return null;

        var targetLeaf = await _ratingRepo.GetLeafInfoAsync(ratingId);
        if (targetLeaf is null || targetLeaf.MerkleLeafHash is null)
            return null;

        return await BuildInclusionProofAsync(anchor, targetLeaf, ratingId);
    }

    // Pure proof construction against a given anchor (no DB access beyond the rating repo), so it
    // can be unit-tested without a database.
    internal async Task<InclusionProofResult?> BuildInclusionProofAsync(
        MerkleAnchor anchor, RatingLeafInfo targetLeaf, Guid ratingId)
    {
        // Build (or reuse) the anchored snapshot. The anchored set is immutable, so the rebuilt
        // tree is cached by anchor root — repeated proofs against the same anchor skip the
        // O(n) reload + rehash of up to 100k leaves (DoS mitigation).
        var snapshot = await GetOrBuildSnapshotAsync(anchor);
        if (snapshot is null)
            return null;

        // Rating exists but was created after the latest anchor → not yet provable.
        if (!snapshot.Index.TryGetValue(ratingId, out var targetIndex))
            return null;

        // The rebuilt root must match the published anchor; otherwise the snapshot has drifted and
        // handing out a proof that won't verify would be worse than a 404.
        if (!string.Equals(snapshot.Tree.RootHex, anchor.MerkleRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var proof = snapshot.Tree.GetInclusionProof(targetIndex);
        var leafHash = MerkleTree.ComputeLeafHash(targetLeaf.Id, targetLeaf.ServiceDid, targetLeaf.CreatedAt);

        return new InclusionProofResult
        {
            RatingId = ratingId.ToString(),
            LeafHash = Convert.ToHexString(leafHash).ToLowerInvariant(),
            MerkleRoot = anchor.MerkleRoot,
            Proof = proof.Select(p => new ProofNodeDto(p.HashHex, p.IsRight)).ToList(),
            LeafIndex = targetIndex,
            TotalLeaves = snapshot.Index.Count,
        };
    }

    private async Task<MerkleSnapshot?> GetOrBuildSnapshotAsync(MerkleAnchor anchor)
    {
        var cacheKey = $"merkle-snapshot:{anchor.MerkleRoot}:{anchor.LeafCount}";
        return await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2);

            var leaves = await _ratingRepo.GetAnchoredLeafHashesAsync(anchor.LeafCount);
            if (leaves.Count == 0)
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10); // don't pin a null
                return null;
            }

            var tree = new MerkleTree();
            var index = new Dictionary<Guid, int>(leaves.Count);
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                tree.AddLeafHash(MerkleTree.ComputeLeafHash(leaf.Id, leaf.ServiceDid, leaf.CreatedAt));
                index[leaf.Id] = i;
            }
            return new MerkleSnapshot(tree, index);
        });
    }
}

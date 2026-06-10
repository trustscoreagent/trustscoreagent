using Dapper;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class AuditService : IAuditService
{
    private readonly DbConnectionFactory _db;
    private readonly IRatingRepository _ratingRepo;

    public AuditService(DbConnectionFactory db, IRatingRepository ratingRepo)
    {
        _db = db;
        _ratingRepo = ratingRepo;
    }

    public async Task RecordLeafAsync(Guid ratingId, string serviceDid, DateTimeOffset timestamp)
    {
        var leafHash = MerkleTree.ComputeLeafHash(ratingId, serviceDid, timestamp);
        var leafHex = Convert.ToHexString(leafHash).ToLowerInvariant();

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE ratings SET merkle_leaf_hash = @LeafHash WHERE id = @RatingId",
            new { LeafHash = leafHex, RatingId = ratingId });
    }

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

        // First leaf_count leaves in deterministic (created_at, id) order = the anchored set.
        var leaves = await _ratingRepo.GetAnchoredLeafHashesAsync(anchor.LeafCount);
        if (leaves.Count == 0)
            return null;

        var tree = new MerkleTree();
        var targetIndex = -1;
        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            tree.AddLeafHash(MerkleTree.ComputeLeafHash(leaf.Id, leaf.ServiceDid, leaf.CreatedAt));
            if (leaf.Id == ratingId)
                targetIndex = i;
        }

        // Rating exists but was created after the latest anchor → not yet provable.
        if (targetIndex == -1)
            return null;

        // The rebuilt root must match the published anchor; otherwise the snapshot has drifted and
        // handing out a proof that won't verify would be worse than a 404.
        if (!string.Equals(tree.RootHex, anchor.MerkleRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var proof = tree.GetInclusionProof(targetIndex);
        var leafHash = MerkleTree.ComputeLeafHash(targetLeaf.Id, targetLeaf.ServiceDid, targetLeaf.CreatedAt);

        return new InclusionProofResult
        {
            RatingId = ratingId.ToString(),
            LeafHash = Convert.ToHexString(leafHash).ToLowerInvariant(),
            MerkleRoot = anchor.MerkleRoot,
            Proof = proof.Select(p => new ProofNodeDto(p.HashHex, p.IsRight)).ToList(),
            LeafIndex = targetIndex,
            TotalLeaves = leaves.Count,
        };
    }
}

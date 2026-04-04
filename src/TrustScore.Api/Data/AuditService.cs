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
        // Get the target rating's leaf info
        var targetLeaf = await _ratingRepo.GetLeafInfoAsync(ratingId);
        if (targetLeaf is null || targetLeaf.MerkleLeafHash is null)
            return null;

        // Get all leaf hashes in order to rebuild the tree
        var allLeaves = await _ratingRepo.GetAllLeafHashesAsync();
        if (allLeaves.Count == 0)
            return null;

        // Rebuild the Merkle tree
        var tree = new MerkleTree();
        var targetIndex = -1;

        for (int i = 0; i < allLeaves.Count; i++)
        {
            var leaf = allLeaves[i];
            var hash = MerkleTree.ComputeLeafHash(leaf.Id, leaf.ServiceDid, leaf.CreatedAt);
            tree.AddLeafHash(hash);

            if (leaf.Id == ratingId)
                targetIndex = i;
        }

        if (targetIndex == -1)
            return null;

        // Generate the proof
        var proof = tree.GetInclusionProof(targetIndex);
        var leafHash = MerkleTree.ComputeLeafHash(targetLeaf.Id, targetLeaf.ServiceDid, targetLeaf.CreatedAt);

        return new InclusionProofResult
        {
            RatingId = ratingId.ToString(),
            LeafHash = Convert.ToHexString(leafHash).ToLowerInvariant(),
            MerkleRoot = tree.RootHex!,
            Proof = proof.Select(p => new ProofNodeDto(p.HashHex, p.IsRight)).ToList(),
            LeafIndex = targetIndex,
            TotalLeaves = allLeaves.Count,
        };
    }
}

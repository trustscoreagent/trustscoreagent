using Dapper;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class AuditService : IAuditService
{
    private readonly DbConnectionFactory _db;

    public AuditService(DbConnectionFactory db)
    {
        _db = db;
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
}

using Dapper;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Data;

public sealed class RatingRepository : IRatingRepository
{
    private readonly DbConnectionFactory _db;

    public RatingRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    public async Task InsertAsync(Rating rating)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO ratings (id, service_did, agent_did,
                status_code, latency_ms, response_size_bytes, schema_valid,
                quality_score, comment, has_receipt, receipt_verified, weight, created_at)
            VALUES (@Id, @ServiceDid, @AgentDid,
                @StatusCode, @LatencyMs, @ResponseSizeBytes, @SchemaValid,
                @QualityScore, @Comment, @HasReceipt, @ReceiptVerified, @Weight, @CreatedAt)
            """,
            new
            {
                rating.Id,
                rating.ServiceDid,
                rating.AgentDid,
                rating.Metrics.StatusCode,
                rating.Metrics.LatencyMs,
                rating.Metrics.ResponseSizeBytes,
                rating.Metrics.SchemaValid,
                rating.QualityScore,
                rating.Comment,
                rating.HasReceipt,
                rating.ReceiptVerified,
                rating.Weight,
                rating.CreatedAt,
            });
    }

    public async Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM ratings
            WHERE agent_did = @AgentDid
              AND service_did = @ServiceDid
              AND created_at > @Since
            """,
            new
            {
                AgentDid = agentDid,
                ServiceDid = serviceDid,
                Since = DateTimeOffset.UtcNow - window,
            });
    }

    public async Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<RatingLeafInfo>(
            """
            SELECT id AS Id,
                   service_did AS ServiceDid,
                   created_at AS CreatedAt,
                   merkle_leaf_hash AS MerkleLeafHash
            FROM ratings
            WHERE id = @Id
            """,
            new { Id = ratingId });
    }

    public async Task<IReadOnlyList<RatingLeafInfo>> GetAllLeafHashesAsync()
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<RatingLeafInfo>(
            """
            SELECT id AS Id,
                   service_did AS ServiceDid,
                   created_at AS CreatedAt,
                   merkle_leaf_hash AS MerkleLeafHash
            FROM ratings
            WHERE merkle_leaf_hash IS NOT NULL
            ORDER BY created_at ASC
            """);
        return results.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<RatingSummary>> GetHistoryAsync(string serviceDid, int months)
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<RatingSummary>(
            """
            SELECT created_at AS CreatedAt,
                   status_code AS StatusCode,
                   latency_ms AS LatencyMs,
                   schema_valid AS SchemaValid,
                   quality_score AS QualityScore,
                   has_receipt AS HasReceipt,
                   receipt_verified AS ReceiptVerified,
                   weight AS Weight
            FROM ratings
            WHERE service_did = @ServiceDid
              AND created_at > @Since
            ORDER BY created_at DESC
            """,
            new
            {
                ServiceDid = serviceDid,
                Since = DateTimeOffset.UtcNow.AddMonths(-months),
            });
        return results.ToList().AsReadOnly();
    }
}

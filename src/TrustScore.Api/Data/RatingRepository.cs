using System.Data;
using Dapper;
using TrustScore.Core.Audit;
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

    // The Merkle leaf hash is written in the same INSERT as the rating, so a rating can never
    // exist without its audit leaf (previously a separate UPDATE that could fail independently).
    private const string InsertSql =
        """
        INSERT INTO ratings (id, service_did, agent_did,
            status_code, latency_ms, response_size_bytes, schema_valid,
            quality_score, comment, has_receipt, receipt_verified, weight, created_at, merkle_leaf_hash)
        VALUES (@Id, @ServiceDid, @AgentDid,
            @StatusCode, @LatencyMs, @ResponseSizeBytes, @SchemaValid,
            @QualityScore, @Comment, @HasReceipt, @ReceiptVerified, @Weight, @CreatedAt, @MerkleLeafHash)
        """;

    private static object BuildInsertParams(Rating rating)
    {
        var leafHash = Convert.ToHexString(
            MerkleTree.ComputeLeafHash(rating.Id, rating.ServiceDid, rating.CreatedAt)).ToLowerInvariant();
        return new
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
            MerkleLeafHash = leafHash,
        };
    }

    public async Task InsertAsync(Rating rating)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(InsertSql, BuildInsertParams(rating));
    }

    public Task InsertAsync(IDbConnection conn, IDbTransaction tx, Rating rating)
        => conn.ExecuteAsync(InsertSql, BuildInsertParams(rating), tx);

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
            ORDER BY created_at, id
            LIMIT 100000
            """);
        return results.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<RatingLeafInfo>> GetAnchoredLeafHashesAsync(int leafCount)
    {
        using var conn = _db.CreateConnection();
        // The anchored snapshot is the first `leafCount` leaves in the same deterministic order
        // used by the hourly anchoring job. Because leaves are append-only, this reproduces the
        // exact set the anchor's root was computed over.
        var results = await conn.QueryAsync<RatingLeafInfo>(
            """
            SELECT id AS Id,
                   service_did AS ServiceDid,
                   created_at AS CreatedAt,
                   merkle_leaf_hash AS MerkleLeafHash
            FROM ratings
            WHERE merkle_leaf_hash IS NOT NULL
            ORDER BY created_at, id
            LIMIT @LeafCount
            """,
            new { LeafCount = leafCount });
        return results.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<DailyHistoryPoint>> GetDailyHistoryAsync(string serviceDid, int months)
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<DailyHistoryPoint>(
            """
            SELECT date_trunc('day', created_at)::date AS Date,
                   COUNT(*) AS RatingsCount,
                   COALESCE(AVG(latency_ms), 0)::int AS AvgLatencyMs,
                   COALESCE(AVG(CASE WHEN status_code BETWEEN 200 AND 299 THEN 1.0 ELSE 0.0 END), 0) AS SuccessRate,
                   COALESCE(AVG(quality_score), 0) AS AvgQuality,
                   COUNT(*) FILTER (WHERE receipt_verified) AS VerifiedCount
            FROM ratings
            WHERE service_did = @ServiceDid
              AND created_at > @Since
            GROUP BY date_trunc('day', created_at)
            ORDER BY 1
            """,
            new
            {
                ServiceDid = serviceDid,
                Since = DateTimeOffset.UtcNow.AddMonths(-months),
            });
        return results.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<AgentRatingRecord>> GetAllRatingsForTrustAsync()
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<AgentRatingRecord>(
            """
            SELECT agent_did AS AgentDid,
                   service_did AS ServiceDid,
                   status_code AS StatusCode,
                   latency_ms AS LatencyMs,
                   schema_valid AS SchemaValid,
                   receipt_verified AS ReceiptVerified
            FROM ratings
            WHERE created_at > NOW() - INTERVAL '90 days'
            ORDER BY created_at ASC
            LIMIT 100000
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
            LIMIT 10000
            """,
            new
            {
                ServiceDid = serviceDid,
                Since = DateTimeOffset.UtcNow.AddMonths(-months),
            });
        return results.ToList().AsReadOnly();
    }
}

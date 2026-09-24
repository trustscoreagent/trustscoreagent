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
            quality_score, comment, has_receipt, receipt_verified, signature_verified,
            weight, created_at, merkle_leaf_hash, leaf_version)
        VALUES (@Id, @ServiceDid, @AgentDid,
            @StatusCode, @LatencyMs, @ResponseSizeBytes, @SchemaValid,
            @QualityScore, @Comment, @HasReceipt, @ReceiptVerified, @SignatureVerified,
            @Weight, @CreatedAt, @MerkleLeafHash, @LeafVersion)
        """;

    // PostgreSQL TIMESTAMPTZ has microsecond resolution, but .NET ticks are 100 ns, so a raw
    // CreatedAt would be hashed at 100 ns precision yet stored (and re-read by the anchoring job) at
    // µs precision — the stored merkle_leaf_hash would then never match the anchored leaf. Truncate
    // to microseconds once and use that single value for both the hash and the stored timestamp.
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), value.Offset);

    /// <summary>
    /// The v2 audit leaf a rating is stored with. The timestamp and weight are normalized first and
    /// the SAME normalized values are stored, so the anchoring job, reading the row back, rebuilds
    /// exactly this leaf.
    /// </summary>
    internal static RatingLeaf LeafFor(Rating rating) => new(
        rating.Id,
        rating.ServiceDid,
        TruncateToMicroseconds(rating.CreatedAt),
        RatingLeaf.CurrentVersion,
        rating.Metrics.StatusCode,
        rating.Metrics.LatencyMs,
        rating.Metrics.ResponseSizeBytes,
        rating.Metrics.SchemaValid,
        rating.QualityScore,
        rating.HasReceipt,
        rating.ReceiptVerified,
        rating.SignatureVerified,
        RatingLeaf.NormalizeWeight(rating.Weight));

    private static object BuildInsertParams(Rating rating)
    {
        var leaf = LeafFor(rating);
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
            rating.SignatureVerified,
            leaf.Weight,
            leaf.CreatedAt,
            MerkleLeafHash = leaf.HashHex(),
            LeafVersion = (short)leaf.LeafVersion,
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

    // Every field a leaf commits to plus the stored leaf hash, in StoredLeaf's constructor order: Dapper binds a positional
    // record by constructor, so the order and the exact CLR types matter (SMALLINT is widened to
    // int here for that reason).
    private const string LeafColumns =
        "id AS Id, service_did AS ServiceDid, created_at AS CreatedAt, " +
        "leaf_version::int AS LeafVersion, status_code AS StatusCode, latency_ms AS LatencyMs, " +
        "response_size_bytes AS ResponseSizeBytes, schema_valid AS SchemaValid, " +
        "quality_score::int AS QualityScore, has_receipt AS HasReceipt, " +
        "receipt_verified AS ReceiptVerified, signature_verified AS SignatureVerified, " +
        "weight AS Weight, merkle_leaf_hash AS MerkleLeafHash";

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

    public async Task<IReadOnlyList<StoredLeaf>> GetLeavesUpToAsync(DateTimeOffset cutoff)
    {
        using var conn = _db.CreateConnection();
        // Every anchored leaf up to the cutoff, in deterministic (created_at, id) order. The cutoff
        // is far enough in the past that all transactions with created_at <= cutoff have committed,
        // so this set is stable and reproduces the anchored root exactly.
        var results = await conn.QueryAsync<StoredLeaf>(
            $"""
            SELECT {LeafColumns}
            FROM ratings
            WHERE merkle_leaf_hash IS NOT NULL
              AND created_at <= @Cutoff
            ORDER BY created_at, id
            """,
            new { Cutoff = cutoff });
        return results.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<StoredLeaf>> GetFirstLeavesAsync(int leafCount)
    {
        using var conn = _db.CreateConnection();
        // Legacy reproduction for anchors stored before the cutoff column existed.
        var results = await conn.QueryAsync<StoredLeaf>(
            $"""
            SELECT {LeafColumns}
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
            -- Explicit casts: DailyHistoryPoint is a positional record, so Dapper binds these by
            -- constructor parameter and needs the exact CLR types (COUNT() is bigint, AVG() is
            -- numeric — neither converts implicitly through a constructor).
            -- ::date alone comes back as DateOnly in Npgsql; ::timestamp yields the DateTime the
            -- record declares.
            SELECT date_trunc('day', created_at)::date::timestamp AS Date,
                   COUNT(*)::int AS RatingsCount,
                   COALESCE(AVG(latency_ms), 0)::int AS AvgLatencyMs,
                   COALESCE(AVG(CASE WHEN status_code BETWEEN 200 AND 299 THEN 1.0 ELSE 0.0 END), 0)::float8 AS SuccessRate,
                   COALESCE(AVG(quality_score), 0)::float8 AS AvgQuality,
                   (COUNT(*) FILTER (WHERE receipt_verified))::int AS VerifiedCount
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
                   receipt_verified AS ReceiptVerified,
                   signature_verified AS SignatureVerified
            FROM ratings
            WHERE created_at > NOW() - INTERVAL '90 days'
            ORDER BY created_at DESC
            LIMIT 100000
            """);
        // DESC so that if the 90-day window exceeds the cap we keep the 100k MOST RECENT ratings
        // (including any current-day Sybil activity), not the oldest as before.
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
                   quality_score::int AS QualityScore,
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

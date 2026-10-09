using Dapper;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class UsageStatsRepository : IUsageStatsRepository
{
    private readonly DbConnectionFactory _db;

    public UsageStatsRepository(DbConnectionFactory db) => _db = db;

    // Positional record: Dapper binds by constructor, so the column order and types (bigint) matter.
    public async Task<RatingStats> GetRatingStatsAsync(DateTimeOffset since, string probeAgentDid)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleAsync<RatingStats>(
            """
            SELECT COUNT(*)                                                    AS Total,
                   COUNT(*) FILTER (WHERE agent_did = @Probe)                  AS FromProbe,
                   COUNT(*) FILTER (WHERE agent_did <> @Probe)                 AS FromAgents,
                   COUNT(DISTINCT agent_did) FILTER (WHERE agent_did <> @Probe) AS DistinctAgents,
                   COUNT(*) FILTER (WHERE signature_verified)                  AS Signed,
                   COUNT(*) FILTER (WHERE receipt_verified)                    AS WithVerifiedReceipt
            FROM ratings
            WHERE created_at >= @Since
            """,
            new { Since = since, Probe = probeAgentDid });
    }
}

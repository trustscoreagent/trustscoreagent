using System.Globalization;
using Dapper;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class AgentRepository : IAgentRepository
{
    private readonly DbConnectionFactory _db;
    private readonly ICacheService _cache;

    public AgentRepository(DbConnectionFactory db, ICacheService cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<double> GetTrustScoreAsync(string agentDid)
    {
        // Try cache first. Parse/format with InvariantCulture so the cached value is readable
        // regardless of the server's locale (a fr-FR "0,5000" would otherwise fail to parse).
        var cached = await _cache.GetAsync($"agent-trust:{agentDid}");
        if (cached is not null && double.TryParse(cached, NumberStyles.Float, CultureInfo.InvariantCulture, out var cachedScore))
            return cachedScore;

        using var conn = _db.CreateConnection();
        var score = await conn.ExecuteScalarAsync<double?>(
            "SELECT trust_score FROM agents WHERE did = @Did",
            new { Did = agentDid });

        var result = score ?? 0.5; // Default: neutral

        await _cache.SetAsync($"agent-trust:{agentDid}", result.ToString("F4", CultureInfo.InvariantCulture), TimeSpan.FromHours(1));
        return result;
    }

    public async Task UpsertTrustScoresAsync(Dictionary<string, double> scores)
    {
        if (scores.Count == 0)
            return;

        // Single batched upsert (UNNEST) in one round-trip + transaction, instead of one
        // INSERT per agent. A partial failure rolls back rather than leaving a half-updated cycle.
        var dids = scores.Keys.ToArray();
        var values = scores.Values.ToArray();

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO agents (did, trust_score, ratings_count)
            SELECT did, trust_score, 0
            FROM unnest(@Dids, @Scores) AS t(did, trust_score)
            ON CONFLICT (did) DO UPDATE SET trust_score = EXCLUDED.trust_score
            """,
            new { Dids = dids, Scores = values });

        foreach (var (did, trustScore) in scores)
            await _cache.SetAsync($"agent-trust:{did}", trustScore.ToString("F4", CultureInfo.InvariantCulture), TimeSpan.FromHours(1));
    }
}

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
        // Try cache first
        var cached = await _cache.GetAsync($"agent-trust:{agentDid}");
        if (cached is not null && double.TryParse(cached, out var cachedScore))
            return cachedScore;

        using var conn = _db.CreateConnection();
        var score = await conn.ExecuteScalarAsync<double?>(
            "SELECT trust_score FROM agents WHERE did = @Did",
            new { Did = agentDid });

        var result = score ?? 0.5; // Default: neutral

        await _cache.SetAsync($"agent-trust:{agentDid}", result.ToString("F4"), TimeSpan.FromHours(1));
        return result;
    }

    public async Task UpsertTrustScoresAsync(Dictionary<string, double> scores)
    {
        using var conn = _db.CreateConnection();
        foreach (var (did, trustScore) in scores)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO agents (did, trust_score, ratings_count)
                VALUES (@Did, @TrustScore, 0)
                ON CONFLICT (did) DO UPDATE SET trust_score = @TrustScore
                """,
                new { Did = did, TrustScore = trustScore });

            // Update cache
            await _cache.SetAsync($"agent-trust:{did}", trustScore.ToString("F4"), TimeSpan.FromHours(1));
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.Options;
using TrustScore.Api.Jobs;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class StatsEndpoints
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static void MapStatsEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/stats", async (
            int? days,
            IUsageCounter usage,
            IUsageStatsRepository stats,
            ICacheService cache,
            IOptions<SeedProbeOptions> probe) =>
        {
            var window = Math.Clamp(days ?? 7, 1, 90);
            var cacheKey = $"stats:{window}";
            if (await cache.GetAsync(cacheKey) is { } cached)
                return Results.Text(cached, "application/json");

            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(window - 1));
            var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            var ratings = await stats.GetRatingStatsAsync(since, probe.Value.AgentDid);
            var counts = await usage.ReadAsync(from, to);

            var response = new
            {
                period = new { from, to, days = window },
                ratings = new
                {
                    total = ratings.Total,
                    // The registry's own probe, which measures public APIs every 6 hours.
                    from_probe = ratings.FromProbe,
                    from_agents = ratings.FromAgents,
                    distinct_agents = ratings.DistinctAgents,
                    signed = ratings.Signed,
                    with_verified_receipt = ratings.WithVerifiedReceipt,
                },
                requests = new
                {
                    total = counts.Sum(c => c.Calls),
                    by_endpoint = counts.GroupBy(c => c.Endpoint)
                        .OrderByDescending(g => g.Sum(c => c.Calls))
                        .ToDictionary(g => g.Key, g => g.Sum(c => c.Calls)),
                    by_client = counts.GroupBy(c => c.Client)
                        .OrderByDescending(g => g.Sum(c => c.Calls))
                        .ToDictionary(g => g.Key, g => g.Sum(c => c.Calls)),
                },
                note = "Aggregate counts only: no IP address, agent DID or request content is kept. " +
                    "Clients are grouped by User-Agent; the registry's MCP server and framework tools " +
                    "identify themselves as trustscoreagent-mcp/<version>, trustscoreagent-langchain/<version> " +
                    "and trustscoreagent-crewai/<version>.",
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            await cache.SetAsync(cacheKey, json, CacheTtl);
            return Results.Text(json, "application/json");
        })
        .WithName("GetStats")
        .WithTags("Stats")
        .Produces(200)
        .WithSummary("How much the registry is used")
        .WithDescription("Aggregate usage over the last `days` days (1-90, default 7): ratings by origin " +
            "(the registry's probe or other agents), distinct rating agents, signed and receipt-backed " +
            "ratings, and API calls by endpoint and client family. No personal data.");
    }
}

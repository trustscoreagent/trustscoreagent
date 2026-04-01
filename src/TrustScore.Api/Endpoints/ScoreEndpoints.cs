using System.Text.Json;
using StackExchange.Redis;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class ScoreEndpoints
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static void MapScoreEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/score", async (
            string did,
            IServiceRepository serviceRepo,
            IScoringEngine scoringEngine,
            IConnectionMultiplexer redis) =>
        {
            if (string.IsNullOrWhiteSpace(did))
                return Results.BadRequest(new { error = "missing_did", message = "Query parameter 'did' is required" });

            // Try Redis cache first
            try
            {
                var redisDb = redis.GetDatabase();
                var cached = await redisDb.StringGetAsync($"score:{did}");
                if (cached.HasValue)
                {
                    var cachedScore = JsonSerializer.Deserialize<object>(cached!);
                    return Results.Ok(cachedScore);
                }
            }
            catch
            {
                // Redis unavailable — continue with DB
            }

            // Lookup in PostgreSQL
            var service = await serviceRepo.GetByDidAsync(did);
            if (service is null)
                return Results.NotFound(new { error = "service_not_found", message = "No ratings found for this service DID" });

            var score = scoringEngine.CalculateScore(service);

            var response = new
            {
                service = score.ServiceDid,
                score = score.Score,
                confidence = score.Confidence,
                ratings_count = score.RatingsCount,
                dimensions = new
                {
                    availability = score.Dimensions.Availability,
                    latency = score.Dimensions.Latency,
                    conformity = score.Dimensions.Conformity,
                },
                recent_incidents = score.RecentIncidents,
                last_rated = score.LastRatedAt,
                service_supports_receipts = score.ServiceSupportsReceipts,
            };

            // Cache the result
            try
            {
                var redisDb = redis.GetDatabase();
                var json = JsonSerializer.Serialize(response);
                await redisDb.StringSetAsync($"score:{did}", json, CacheTtl);
            }
            catch
            {
                // Redis unavailable — continue without cache
            }

            return Results.Ok(response);
        })
        .WithName("GetScore")
        .WithTags("Score")
        .Produces(200)
        .Produces(404)
        .WithOpenApi(op =>
        {
            op.Summary = "Get trust score for a microservice";
            op.Description = "Returns the reputation score, confidence level, and dimensional breakdown for a given service DID. Use this BEFORE calling any untrusted external service.";
            return op;
        });
    }
}

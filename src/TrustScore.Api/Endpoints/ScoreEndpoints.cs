using System.Text.Json;
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
            ICacheService cache) =>
        {
            if (string.IsNullOrWhiteSpace(did))
                return Results.BadRequest(new { error = "missing_did", message = "Query parameter 'did' is required" });

            // Try cache first
            var cached = await cache.GetAsync($"score:{did}");
            if (cached is not null)
            {
                var cachedScore = JsonSerializer.Deserialize<object>(cached);
                return Results.Ok(cachedScore);
            }

            var service = await serviceRepo.GetByDidAsync(did);

            // Unknown service → return neutral score (0.5) with zero confidence
            // This is smoother for agents than a 404 error
            var score = service is not null
                ? scoringEngine.CalculateScore(service)
                : scoringEngine.CalculateScore(new TrustScore.Core.Models.ServiceEntity { Did = did });

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
                known = service is not null,
            };

            await cache.SetAsync($"score:{did}", JsonSerializer.Serialize(response), CacheTtl);

            return Results.Ok(response);
        })
        .WithName("GetScore")
        .WithTags("Score")
        .Produces(200)
        .WithOpenApi(op =>
        {
            op.Summary = "Get trust score for a microservice";
            op.Description = "Returns the reputation score, confidence level, and dimensional breakdown for a given service DID. Unknown services return a neutral score (0.5) with zero confidence and known=false.";
            return op;
        });
    }
}

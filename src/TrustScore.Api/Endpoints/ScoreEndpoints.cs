using System.Text.Json;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Endpoints;

public static class ScoreEndpoints
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static void MapScoreEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/score", async (
            string? did,
            string? service,
            IServiceRepository serviceRepo,
            IScoringEngine scoringEngine,
            ICacheService cache) =>
        {
            // Accept both ?service= (preferred) and ?did= (backwards compatible)
            var raw = service ?? did;
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { error = "missing_service", message = "Query parameter 'service' (or 'did') is required" });

            var serviceId = ServiceIdentifier.Normalize(raw);

            // Try cache first
            var cached = await cache.GetAsync($"score:{serviceId}");
            if (cached is not null)
            {
                var cachedScore = JsonSerializer.Deserialize<object>(cached);
                return Results.Ok(cachedScore);
            }

            var serviceEntity = await serviceRepo.GetByDidAsync(serviceId);

            // Unknown service → return neutral score (0.5) with zero confidence
            var score = serviceEntity is not null
                ? scoringEngine.CalculateScore(serviceEntity)
                : scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });

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
                known = serviceEntity is not null,
            };

            await cache.SetAsync($"score:{serviceId}", JsonSerializer.Serialize(response), CacheTtl);

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

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

            var isProvider = ServiceIdentifier.IsProviderLevel(serviceId);

            ServiceScore score;
            bool known;

            if (isProvider)
            {
                // Provider-level query: aggregate all endpoints under this domain
                var endpoints = await serviceRepo.GetByProviderAsync(serviceId);

                // Also check if there's a direct entry for the domain itself
                var directEntity = await serviceRepo.GetByDidAsync(serviceId);
                if (directEntity is not null)
                    endpoints = endpoints.Append(directEntity).ToList().AsReadOnly();

                if (endpoints.Count > 0)
                {
                    score = scoringEngine.CalculateProviderScore(serviceId, endpoints);
                    known = true;
                }
                else
                {
                    score = scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });
                    known = false;
                }
            }
            else
            {
                // Endpoint-level query: specific path
                var serviceEntity = await serviceRepo.GetByDidAsync(serviceId);
                known = serviceEntity is not null;
                score = serviceEntity is not null
                    ? scoringEngine.CalculateScore(serviceEntity)
                    : scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });
            }

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
                known,
                level = isProvider ? "provider" : "endpoint",
            };

            await cache.SetAsync($"score:{serviceId}", JsonSerializer.Serialize(response), CacheTtl);

            return Results.Ok(response);
        })
        .WithName("GetScore")
        .WithTags("Score")
        .Produces(200)
        .WithSummary("Get trust score for a microservice")
        .WithDescription("Returns the trust score for a service. " +
            "Pass a full URL (api.example.com/v1/translate) for endpoint-level score, " +
            "or just a domain (api.example.com) for aggregated provider score. " +
            "Unknown services return neutral score (0.5) with known=false.");
    }
}

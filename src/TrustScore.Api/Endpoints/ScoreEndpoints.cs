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
            object? provider = null;

            if (isProvider)
            {
                var aggregate = await ProviderScoreAsync(serviceId, serviceRepo, scoringEngine);
                known = aggregate is not null;
                score = aggregate?.Score ?? scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });
            }
            else
            {
                // Endpoint-level query: specific path
                var serviceEntity = await serviceRepo.GetByDidAsync(serviceId);
                known = serviceEntity is not null;
                score = serviceEntity is not null
                    ? scoringEngine.CalculateScore(serviceEntity)
                    : scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });

                // Nothing on this endpoint: say what is known about the rest of its domain, kept
                // apart from the endpoint's own (neutral) score so it is never mistaken for a
                // measurement of this endpoint.
                if (!known)
                {
                    var providerId = ServiceIdentifier.ExtractProvider(serviceId);
                    if (await ProviderScoreAsync(providerId, serviceRepo, scoringEngine) is { } aggregate)
                    {
                        provider = new
                        {
                            service = providerId,
                            score = aggregate.Score.Score,
                            confidence = aggregate.Score.Confidence,
                            ratings_count = aggregate.Score.RatingsCount,
                            endpoints_rated = aggregate.Endpoints,
                            last_rated = aggregate.Score.LastRatedAt,
                        };
                    }
                }
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
                provider,
            };

            await cache.SetAsync($"score:{serviceId}", JsonSerializer.Serialize(response), CacheTtl);

            return Results.Ok(response);
        })
        .WithName("GetScore")
        .WithTags("Score")
        .Produces(200)
        .WithSummary("Get trust score for a microservice")
        .WithDescription("Returns what other agents measured about a service. " +
            "Call it before using a service you have no history with, and especially before " +
            "paying one: a lookup costs far less than a wasted call or a payment to a service " +
            "that returns nothing useful. If you received a 402 Payment Required from an " +
            "unfamiliar service, this is the check to run before paying. " +
            "Pass a full URL (api.example.com/v1/translate) for endpoint-level score, " +
            "or just a domain (api.example.com) for aggregated provider score. " +
            "Unknown services return a neutral 0.5 with known=false, never a 404; read that as " +
            "unverified rather than as safe. For an unknown endpoint whose domain has rated " +
            "endpoints, 'provider' carries the domain's aggregate score: a hint about the " +
            "operator, not a measurement of that endpoint.");
    }

    /// <summary>
    /// The aggregate score of every rated endpoint under <paramref name="providerId"/> (and of the
    /// domain itself, when it was rated directly), or null when none is.
    /// </summary>
    private static async Task<(ServiceScore Score, int Endpoints)?> ProviderScoreAsync(
        string providerId, IServiceRepository serviceRepo, IScoringEngine scoringEngine)
    {
        var endpoints = await serviceRepo.GetByProviderAsync(providerId);
        var directEntity = await serviceRepo.GetByDidAsync(providerId);
        if (directEntity is not null)
            endpoints = endpoints.Append(directEntity).ToList().AsReadOnly();

        return endpoints.Count > 0
            ? (scoringEngine.CalculateProviderScore(providerId, endpoints), endpoints.Count)
            : null;
    }
}

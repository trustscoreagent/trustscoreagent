using System.Text.Json;
using StackExchange.Redis;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Endpoints;

public static class RateEndpoints
{
    private const int MaxRatingsPerHour = 10;

    public static void MapRateEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/rate", async (
            HttpContext httpContext,
            RateRequest request,
            IServiceRepository serviceRepo,
            IRatingRepository ratingRepo,
            IScoringEngine scoringEngine,
            IConnectionMultiplexer redis) =>
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(request.ServiceDid))
                return Results.BadRequest(new { error = "missing_service_did", message = "Field 'service_did' is required" });

            var agentDid = httpContext.Request.Headers["X-Agent-DID"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(agentDid))
                return Results.BadRequest(new { error = "missing_agent_did", message = "Header 'X-Agent-DID' is required" });

            if (request.Metrics is null)
                return Results.BadRequest(new { error = "missing_metrics", message = "Field 'metrics' is required" });

            if (request.Metrics.LatencyMs <= 0)
                return Results.BadRequest(new { error = "invalid_latency", message = "metrics.latency_ms must be > 0" });

            if (request.Metrics.StatusCode < 100 || request.Metrics.StatusCode > 599)
                return Results.BadRequest(new { error = "invalid_status_code", message = "metrics.status_code must be between 100 and 599" });

            if (request.QualityScore.HasValue && (request.QualityScore < 1 || request.QualityScore > 5))
                return Results.BadRequest(new { error = "invalid_quality_score", message = "quality_score must be between 1 and 5" });

            // Rate limiting
            var recentCount = await ratingRepo.CountRecentAsync(agentDid, request.ServiceDid, TimeSpan.FromHours(1));
            if (recentCount >= MaxRatingsPerHour)
                return Results.Json(
                    new { error = "rate_limited", message = $"Maximum {MaxRatingsPerHour} ratings per agent per service per hour" },
                    statusCode: 429);

            // Determine weight based on receipt
            var hasReceipt = !string.IsNullOrWhiteSpace(request.Receipt);
            var receiptVerified = false;
            var weight = 0.3;

            if (hasReceipt)
            {
                // Phase 1: basic receipt presence gives weight 0.7
                // Phase 2: full JWT verification gives weight 1.0
                weight = 0.7;
                // TODO Phase 2: verify JWT signature via ReceiptVerifier
            }

            var rating = new Rating
            {
                ServiceDid = request.ServiceDid,
                AgentDid = agentDid,
                Metrics = new RatingMetrics
                {
                    StatusCode = request.Metrics.StatusCode,
                    LatencyMs = request.Metrics.LatencyMs,
                    ResponseSizeBytes = request.Metrics.ResponseSizeBytes,
                    SchemaValid = request.Metrics.SchemaValid,
                },
                QualityScore = request.QualityScore,
                Comment = request.Comment,
                Receipt = request.Receipt,
                HasReceipt = hasReceipt,
                ReceiptVerified = receiptVerified,
                Weight = weight,
            };

            // Get or create service entity
            var service = await serviceRepo.GetByDidAsync(request.ServiceDid)
                ?? new ServiceEntity { Did = request.ServiceDid };

            // Apply rating to scoring model
            service = scoringEngine.ApplyRating(service, rating);

            // Persist — service first (ratings has FK to services)
            await serviceRepo.UpsertAsync(service);
            await ratingRepo.InsertAsync(rating);

            // Invalidate cache
            try
            {
                var redisDb = redis.GetDatabase();
                await redisDb.KeyDeleteAsync($"score:{request.ServiceDid}");
            }
            catch
            {
                // Redis unavailable — cache will expire naturally
            }

            var score = scoringEngine.CalculateScore(service);

            return Results.Ok(new
            {
                accepted = true,
                rating_weight = hasReceipt ? "verified" : "unverified",
                new_score = score.Score,
            });
        })
        .WithName("SubmitRating")
        .WithTags("Rating")
        .Produces(200)
        .Produces(400)
        .Produces(429)
        .WithOpenApi(op =>
        {
            op.Summary = "Submit a rating for a microservice";
            op.Description = "Rate a microservice after calling it. Provide technical metrics from your interaction. Include the receipt from the X-Trust-Receipt header if the service provided one for higher rating weight.";
            return op;
        });
    }
}

public sealed record RateRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("service_did")]
    public string? ServiceDid { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("metrics")]
    public RateRequestMetrics? Metrics { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("quality_score")]
    public int? QualityScore { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("receipt")]
    public string? Receipt { get; init; }
}

public sealed record RateRequestMetrics
{
    [System.Text.Json.Serialization.JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("latency_ms")]
    public int LatencyMs { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("response_size_bytes")]
    public int? ResponseSizeBytes { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("schema_valid")]
    public bool? SchemaValid { get; init; }
}

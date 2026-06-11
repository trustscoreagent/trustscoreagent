using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using ReceiptStatus = TrustScore.Core.Models.ReceiptVerificationStatus;
using SvcId = TrustScore.Core.Models.ServiceIdentifier;

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
            IScoringEngine scoringEngine,
            ICacheService cache,
            IRateLimiter rateLimiter,
            IReceiptVerifier receiptVerifier,
            IRatingWriter ratingWriter,
            IAgentRepository agentRepo) =>
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(request.ServiceDid))
                return Results.BadRequest(new { error = "missing_service_did", message = "Field 'service_did' (or 'service') is required" });

            var serviceId = SvcId.Normalize(request.ServiceDid);

            var agentDid = httpContext.Request.Headers["X-Agent-DID"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(agentDid))
                return Results.BadRequest(new { error = "missing_agent_did", message = "Header 'X-Agent-DID' is required" });

            if (request.Metrics is null)
                return Results.BadRequest(new { error = "missing_metrics", message = "Field 'metrics' is required" });

            if (request.Metrics.LatencyMs <= 0 || request.Metrics.LatencyMs > 600_000)
                return Results.BadRequest(new { error = "invalid_latency", message = "metrics.latency_ms must be between 1 and 600000" });

            if (request.Metrics.StatusCode < 100 || request.Metrics.StatusCode > 599)
                return Results.BadRequest(new { error = "invalid_status_code", message = "metrics.status_code must be between 100 and 599" });

            if (request.QualityScore.HasValue && (request.QualityScore < 1 || request.QualityScore > 5))
                return Results.BadRequest(new { error = "invalid_quality_score", message = "quality_score must be between 1 and 5" });

            if (request.Comment is not null && request.Comment.Length > 500)
                return Results.BadRequest(new { error = "comment_too_long", message = "comment must be 500 characters or less" });

            if (agentDid.Length > 500)
                return Results.BadRequest(new { error = "invalid_agent_did", message = "X-Agent-DID too long" });

            // Rate limiting via Redis
            var rateLimitKey = $"{agentDid}:{serviceId}";
            var rateLimitResult = await rateLimiter.CheckAsync(rateLimitKey, MaxRatingsPerHour, TimeSpan.FromHours(1));
            if (!rateLimitResult.Allowed)
                return Results.Json(
                    new { error = "rate_limited", message = $"Maximum {MaxRatingsPerHour} ratings per agent per service per hour", remaining = rateLimitResult.Remaining },
                    statusCode: 429);

            // Verify receipt if provided. Per spec §5, an unverified rating still counts, but at a
            // reduced base weight (0.3); a verified receipt grants full weight (1.0). The base
            // weight is then scaled by the agent's EigenTrust score (spec §6.2). MVP Sybil
            // resistance comes from rate limiting + the hourly EigenTrust recompute (a self-
            // asserted X-Agent-DID converges toward low trust); mandatory per-request agent
            // signatures (X-Agent-Signature) are a Phase 2 item.
            var hasReceipt = !string.IsNullOrWhiteSpace(request.Receipt);
            var receiptVerified = false;
            var weight = 0.3;

            if (hasReceipt)
            {
                var verification = await receiptVerifier.VerifyAsync(request.Receipt!, SvcId.ToDid(serviceId));

                if (verification.Status == ReceiptStatus.NonceAlreadyUsed)
                    return Results.Json(
                        new { error = "nonce_replay", message = "This receipt has already been used" },
                        statusCode: 400);

                weight = verification.Weight;
                receiptVerified = verification.IsVerified;
            }

            // Apply agent trust score (EigenTrust) to rating weight.
            var agentTrust = await agentRepo.GetTrustScoreAsync(agentDid);
            weight *= agentTrust;

            var rating = new Rating
            {
                ServiceDid = serviceId,
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

            // Compute the rating delta and persist the score update together with the rating
            // record (which carries its own Merkle leaf hash) in a single transaction. A partial
            // write would otherwise leave the aggregate score permanently out of sync with the
            // ratings table, or drop the rating from the audit tree.
            var delta = scoringEngine.ComputeDelta(rating);
            await ratingWriter.SubmitAsync(serviceId, delta, rating);

            // Invalidate cache
            await cache.RemoveAsync($"score:{serviceId}");

            // Read back fresh score for response
            var service = await serviceRepo.GetByDidAsync(serviceId);
            var score = service is not null
                ? scoringEngine.CalculateScore(service)
                : scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });

            return Results.Ok(new
            {
                accepted = true,
                rating_weight = receiptVerified ? "verified" : "unverified",
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
    // Accept both "service" (preferred) and "service_did" (backwards compatible)
    [System.Text.Json.Serialization.JsonPropertyName("service")]
    public string? Service { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("service_did")]
    public string? ServiceDidLegacy { get; init; }

    public string? ServiceDid => Service ?? ServiceDidLegacy;

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

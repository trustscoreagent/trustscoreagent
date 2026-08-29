using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using ReceiptStatus = TrustScore.Core.Models.ReceiptVerificationStatus;
using SvcId = TrustScore.Core.Models.ServiceIdentifier;

namespace TrustScore.Api.Endpoints;

public static class RateEndpoints
{
    private const int MaxRatingsPerHour = 10;

    /// <summary>
    /// The canonical route, and the exact string clients sign. Routing is case-insensitive, so the
    /// raw request path is whatever spelling the caller (or a proxy) used; signing that instead
    /// would reject a valid signature sent to <c>/V1/Rate</c>. Binding to this constant still ties
    /// a signature to this one endpoint, because no other endpoint verifies against it.
    /// </summary>
    public const string RatePath = "/v1/rate";

    /// <summary>
    /// How much an unsigned rating is discounted. Identity and attestation are orthogonal: a
    /// receipt says the service saw the call, a signature says we know who is reporting it. An
    /// unsigned rating still counts (existing clients keep working) but only half, because its
    /// X-Agent-DID is self-asserted and could name any agent.
    /// </summary>
    private const double UnsignedIdentityFactor = 0.5;

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
            IAgentSignatureVerifier agentSignatureVerifier,
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

            // Verify the agent signature BEFORE the per-agent rate limit, so that limit can be keyed
            // on an identity that was proven rather than one that was merely claimed. Running the
            // crypto ahead of the counter is safe: GlobalRateLimitMiddleware already caps every
            // caller at 120 requests/minute per IP, which bounds both the Ed25519 verification
            // (microseconds) and the single nonce write a request can trigger. It also stays ahead
            // of receipt verification, so a forged submitter is rejected without consuming the
            // receipt's nonce.
            var signatureResult = await agentSignatureVerifier.VerifyAsync(
                new AgentSignatureHeaders(
                    agentDid,
                    httpContext.Request.Headers[AgentSignatureHeaders.SignatureHeader].FirstOrDefault(),
                    httpContext.Request.Headers[AgentSignatureHeaders.TimestampHeader].FirstOrDefault(),
                    httpContext.Request.Headers[AgentSignatureHeaders.NonceHeader].FirstOrDefault()),
                httpContext.Request.Host.Value ?? string.Empty,
                httpContext.Request.Method,
                RatePath,
                await ReadRawBodyAsync(httpContext.Request));

            // A present-but-bad signature is a hard failure, never a silent downgrade to the
            // unsigned path: otherwise sending a junk signature would be the cheapest way to keep
            // impersonating an agent.
            if (signatureResult.IsRejected)
            {
                return Results.Json(
                    new
                    {
                        error = "invalid_agent_signature",
                        message = "X-Agent-Signature did not verify for this request",
                        reason = signatureResult.Status.ToString(),
                    },
                    statusCode: 401);
            }

            // Rate limit on an identity we can hold responsible. The DID keys the bucket only when
            // it was proven: keying an unsigned request on its asserted X-Agent-DID would let anyone
            // lock a real agent out for an hour by sending junk ratings in its name. Unsigned callers
            // are bucketed by IP instead, so they can only spend their own quota, and rotating the
            // claimed DID no longer mints a fresh allowance.
            var (rateLimitKey, rateLimitScope) = signatureResult.IsVerified
                ? ($"agent:{agentDid}:{serviceId}", "agent")
                : ($"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{serviceId}", "caller");

            var rateLimitResult = await rateLimiter.CheckAsync(rateLimitKey, MaxRatingsPerHour, TimeSpan.FromHours(1));
            if (!rateLimitResult.Allowed)
                return Results.Json(
                    new
                    {
                        error = "rate_limited",
                        message = $"Maximum {MaxRatingsPerHour} ratings per {rateLimitScope} per service per hour",
                        remaining = rateLimitResult.Remaining,
                    },
                    statusCode: 429);

            // Verify receipt if provided. Per spec §5, an unverified rating still counts, but at a
            // reduced base weight (0.3); a verified receipt grants full weight (1.0). The base
            // weight is then scaled by the agent's EigenTrust score (spec §6.2).
            var hasReceipt = !string.IsNullOrWhiteSpace(request.Receipt);
            var receiptVerified = false;
            var weight = 0.3;

            if (hasReceipt)
            {
                var verification = await receiptVerifier.VerifyAsync(request.Receipt!, SvcId.ToDid(serviceId), agentDid);

                if (verification.Status == ReceiptStatus.NonceAlreadyUsed)
                    return Results.Json(
                        new { error = "nonce_replay", message = "This receipt has already been used" },
                        statusCode: 400);

                weight = verification.Weight;
                receiptVerified = verification.IsVerified;
            }

            // Discount the rating if we cannot prove who sent it.
            if (!signatureResult.IsVerified)
                weight *= UnsignedIdentityFactor;

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
                SignatureVerified = signatureResult.IsVerified,
                Weight = weight,
            };

            // Compute the rating delta and persist the score update together with the rating
            // record (which carries its own Merkle leaf hash) in a single transaction. A partial
            // write would otherwise leave the aggregate score permanently out of sync with the
            // ratings table, or drop the rating from the audit tree.
            var delta = scoringEngine.ComputeDelta(rating);
            await ratingWriter.SubmitAsync(serviceId, delta, rating);

            // Invalidate cache — both the endpoint-level key and the provider-level aggregate,
            // since an endpoint rating also changes the provider's rolled-up score.
            await cache.RemoveAsync($"score:{serviceId}");
            var provider = SvcId.ExtractProvider(serviceId);
            if (provider != serviceId)
                await cache.RemoveAsync($"score:{provider}");

            // Read back fresh score for response
            var service = await serviceRepo.GetByDidAsync(serviceId);
            var score = service is not null
                ? scoringEngine.CalculateScore(service)
                : scoringEngine.CalculateScore(new ServiceEntity { Did = serviceId });

            return Results.Ok(new
            {
                accepted = true,
                rating_weight = receiptVerified ? "verified" : "unverified",
                agent_identity = signatureResult.IsVerified ? "signed" : "unsigned",
                new_score = score.Score,
            });
        })
        .WithName("SubmitRating")
        .WithTags("Rating")
        .Produces(200)
        .Produces(400)
        .Produces(401)
        .Produces(429)
        .WithSummary("Submit a rating for a microservice")
        .WithDescription("Rate a microservice after calling it. Provide technical metrics from your interaction. Include the receipt from the X-Trust-Receipt header if the service provided one for higher rating weight. Sign the request with your agent key (X-Agent-Signature) so the rating is attributed to you rather than merely claimed; unsigned ratings are accepted at reduced weight.");
    }

    /// <summary>
    /// Re-reads the raw request body, which model binding has already consumed. Returns the exact
    /// bytes the signature covers, so it must not be a re-serialisation of the parsed model.
    /// </summary>
    private static async Task<byte[]> ReadRawBodyAsync(HttpRequest request)
    {
        // Buffering is enabled upstream for this route only. If it somehow was not, fail closed:
        // an empty body yields a different hash, so a signed request is rejected rather than
        // accepted on an unverified body. Unsigned requests are unaffected.
        if (!request.Body.CanSeek)
            return Array.Empty<byte>();

        request.Body.Position = 0;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        request.Body.Position = 0;
        return buffer.ToArray();
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

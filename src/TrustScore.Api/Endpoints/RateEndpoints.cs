using TrustScore.Api.Middleware;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using ReceiptStatus = TrustScore.Core.Models.ReceiptVerificationStatus;
using SvcId = TrustScore.Core.Models.ServiceIdentifier;

namespace TrustScore.Api.Endpoints;

public static class RateEndpoints
{
    private const int MaxRatingsPerHour = 10;

    /// <summary>
    /// Signed ratings per caller IP per provider domain per hour, across every key used from that IP.
    /// Ten times the per-agent limit: room for a handful of honest agents behind one NAT, not for
    /// a fresh keypair per request.
    /// </summary>
    private const int MaxSignedRatingsPerCallerPerHour = 100;

    /// <summary>
    /// The canonical route, and the exact string clients sign. Routing is case-insensitive, so the
    /// raw request path is whatever spelling the caller (or a proxy) used; signing that instead
    /// would reject a valid signature sent to <c>/V1/Rate</c>. Binding to this constant still ties
    /// a signature to this one endpoint, because no other endpoint verifies against it.
    /// </summary>
    public const string RatePath = "/v1/rate";


    /// <summary>
    /// How far back repeat ratings from one source are counted. The n-th rating of a provider from
    /// the same source (caller address, and agent key when signed) within this window counts 1/n:
    /// a source saying the same thing again adds little, and saying it a thousand times must not
    /// outweigh independent observers.
    /// </summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Ratings per provider domain per <see cref="RepeatWindow"/> that count from agents with no
    /// earned trust (new keys, and every unsigned rating, since unsigned identities cost nothing).
    /// Past it a newcomer rating is still recorded, at zero weight: newcomers together can move a
    /// score only so far in a day, however many identities or addresses they use, while agents that
    /// earned trust are not affected.
    /// </summary>
    internal const int NewcomerRatingsPerProviderPerDay = 5;

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
            if (!PublicHost.IsPublic(serviceId))
                return Results.BadRequest(new
                {
                    error = "invalid_service",
                    message = $"'{SvcId.ExtractProvider(serviceId)}' is not a public host: ratings must name a service " +
                        "reachable on the internet, not localhost, a private or link-local address, or a local name",
                });

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

            if (TrustIdentity.IsReserved(agentDid))
                return Results.BadRequest(new
                {
                    error = "reserved_agent_did",
                    message = $"did:web identities on {TrustIdentity.RegistryDomain} belong to the registry's probe and cannot be used to rate",
                });

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
            //
            // A proven DID is still free to mint, though: a caller that signs every request with a
            // fresh key would get a fresh per-agent bucket each time. So signed requests are also
            // counted per caller, with a ceiling generous enough for several honest agents sharing
            // an egress IP but low enough that key rotation stops being a way around the limit.
            //
            // Buckets are per provider domain, not per endpoint: otherwise every path of a domain
            // (evil.com/a1, evil.com/a2, ...) would be a fresh allowance on the same domain's score.
            var caller = ClientAddress.Bucket(httpContext.Connection.RemoteIpAddress);
            var provider = SvcId.ExtractProvider(serviceId);
            var buckets = signatureResult.IsVerified
                ? new[]
                {
                    ($"ip-signed:{caller}:{provider}", MaxSignedRatingsPerCallerPerHour, "caller"),
                    ($"agent:{agentDid}:{provider}", MaxRatingsPerHour, "agent"),
                }
                : new[] { ($"ip:{caller}:{provider}", MaxRatingsPerHour, "caller") };

            foreach (var (key, limit, scope) in buckets)
            {
                var rateLimitResult = await rateLimiter.CheckAsync(key, limit, TimeSpan.FromHours(1));
                if (!rateLimitResult.Allowed)
                    return Results.Json(
                        new
                        {
                            error = "rate_limited",
                            message = $"Maximum {limit} ratings per {scope} per provider domain per hour",
                            remaining = rateLimitResult.Remaining,
                        },
                        statusCode: 429);
            }

            // Verify the receipt if one is provided. A verified receipt for the call this rating
            // reports raises its base weight; see RatingWeight for how signature, trust, repeats and
            // the newcomers' allowance then scale it.
            var hasReceipt = !string.IsNullOrWhiteSpace(request.Receipt);
            var receiptVerified = false;
            var notes = new List<string>();

            if (hasReceipt)
            {
                var verification = await receiptVerifier.VerifyAsync(request.Receipt!, SvcId.ToDid(serviceId), agentDid);

                if (verification.Status == ReceiptStatus.NonceAlreadyUsed)
                    return Results.Json(
                        new { error = "nonce_replay", message = "This receipt has already been used" },
                        statusCode: 400);

                receiptVerified = verification.IsVerified;

                // A receipt vouches for one call: the endpoint it names and the status the service
                // returned. Used for any other endpoint, or with other metrics, it proves nothing
                // about this rating, which then counts as unverified.
                if (receiptVerified && !ReceiptCovers(verification.Payload!, serviceId, request.Metrics.StatusCode))
                {
                    receiptVerified = false;
                    notes.Add("the receipt is valid but covers a different endpoint or status code than this rating, so the rating counts as unverified");
                }
            }

            // The lookup uses the identity this rating actually accrues under: an unsigned rating
            // must not borrow the standing of the DID it names, or naming a reputable agent would be
            // a free weight multiplier.
            var agentTrust = await agentRepo.GetTrustScoreAsync(
                TrustIdentity.For(agentDid, signatureResult.IsVerified));

            // Repeat ratings from the same source saturate: the caller address always identifies
            // the source, the agent key too when it is proven.
            var repeat = 1;
            var sources = signatureResult.IsVerified
                ? new[] { $"repeat:{caller}:{provider}", $"repeat-agent:{agentDid}:{provider}" }
                : new[] { $"repeat:{caller}:{provider}" };
            foreach (var key in sources)
                repeat = Math.Max(repeat, (await rateLimiter.CheckAsync(key, int.MaxValue, RepeatWindow)).CurrentCount);
            if (repeat > 1)
                notes.Add($"repeat rating: rating {repeat} of {provider} from the same source in 24 hours, so it counts 1/{repeat}");

            // Agents without earned trust share a daily allowance per provider.
            var allowanceExhausted = false;
            if (agentTrust <= EigenTrustEngine.DefaultTrustScore)
            {
                var allowance = await rateLimiter.CheckAsync($"newcomers:{provider}", NewcomerRatingsPerProviderPerDay, RepeatWindow);
                allowanceExhausted = !allowance.Allowed;
                if (allowanceExhausted)
                    notes.Add($"recorded at zero weight: ratings of {provider} from agents without earned trust have reached today's limit. " +
                        "Trust is earned by ratings that agree with what trusted agents observe");
            }

            if (receiptVerified && agentTrust <= EigenTrustEngine.DefaultTrustScore)
                notes.Add("the receipt is verified, but it lends full weight only once this agent has earned trust");

            var weight = RatingWeight.Counted(receiptVerified, signatureResult.IsVerified, agentTrust, repeat, allowanceExhausted);

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
                // What GET /v1/audit/proof/{rating_id} takes. Without it a rater has no way to check
                // that the rating it submitted is in the audit log, as submitted.
                rating_id = rating.Id,
                rating_weight = receiptVerified ? "verified" : "unverified",
                agent_identity = signatureResult.IsVerified ? "signed" : "unsigned",
                // Said out loud rather than left for the client to infer: its signature was fine,
                // it just could not be counted as signed this time.
                agent_identity_note = signatureResult.Status == AgentSignatureStatus.ReplayCheckUnavailable
                    ? "signature valid but counted as unsigned: replay protection is temporarily unavailable"
                    : null,
                // The weight the rating counted at, as committed in its audit leaf, and why it is
                // below the base weight when it is.
                effective_weight = Math.Round(weight, 6),
                weight_notes = notes,
                new_score = score.Score,
            });
        })
        // Agents call this from code, not from web pages. Without CORS a page cannot make its
        // visitors' browsers submit ratings from thousands of addresses.
        .WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute())
        .WithName("SubmitRating")
        .WithTags("Rating")
        .Produces(200)
        .Produces(400)
        .Produces(401)
        .Produces(429)
        .WithSummary("Submit a rating for a microservice")
        .WithDescription("Report what happened after calling a service, from metrics you " +
            "already hold. An agent with a stable identity reads back the ratings it writes, so " +
            "this is closer to writing a cache you will query than to donating to a commons: a " +
            "service that wasted a call, or took payment without delivering, is worth the one " +
            "call it takes to record, because otherwise you meet it again with no memory of it. " +
            "Record the ones that worked too, since a score needs both outcomes to mean " +
            "anything. Include the receipt from the X-Trust-Receipt header if the service " +
            "provided one, for full rating weight. Sign the request with your agent key " +
            "(X-Agent-Signature) so the rating is attributed to you rather than merely claimed; " +
            "unsigned ratings are accepted at reduced weight.");
    }

    /// <summary>
    /// Whether a verified receipt vouches for this rating: same status code, and the endpoint the
    /// receipt names is the one rated (any endpoint of the domain when the rating is for the domain).
    /// </summary>
    internal static bool ReceiptCovers(ReceiptPayload receipt, string serviceId, int statusCode)
    {
        if (receipt.StatusCode != statusCode)
            return false;
        if (SvcId.IsProviderLevel(serviceId))
            return true;

        try
        {
            var endpoint = receipt.Endpoint.Trim();
            var url = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? endpoint
                : $"https://{SvcId.ExtractProvider(serviceId)}/{endpoint.TrimStart('/')}";
            return SvcId.Normalize(url) == serviceId;
        }
        catch (ArgumentException)
        {
            return false;
        }
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

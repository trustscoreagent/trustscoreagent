using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Endpoints;

/// <summary>
/// Premium endpoints — currently free, will be gated by x402 micropayments in Phase 2.
/// Each endpoint is annotated with its future price.
/// </summary>
public static class PremiumEndpoints
{
    public static void MapPremiumEndpoints(this WebApplication app)
    {
        // --- Score History (future price: 0.001 USDC) ---
        app.MapGet("/v1/score/history", async (
            string? did,
            string? service,
            int? months,
            IServiceRepository serviceRepo,
            IRatingRepository ratingRepo,
            IScoringEngine scoringEngine) =>
        {
            var raw = service ?? did;
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { error = "missing_service", message = "Query parameter 'service' (or 'did') is required" });

            var serviceId = ServiceIdentifier.Normalize(raw);
            var svc = await serviceRepo.GetByDidAsync(serviceId);
            if (svc is null)
                return Results.NotFound(new { error = "service_not_found", message = "No ratings found for this service" });

            var period = Math.Clamp(months ?? 12, 1, 24);
            var ratings = await ratingRepo.GetHistoryAsync(serviceId, period);

            // Group by day and compute daily aggregates
            var dailyScores = ratings
                .GroupBy(r => r.CreatedAt.Date)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    date = g.Key.ToString("yyyy-MM-dd"),
                    ratings_count = g.Count(),
                    avg_latency_ms = (int)g.Average(r => r.LatencyMs),
                    success_rate = Math.Round(g.Count(r => r.StatusCode >= 200 && r.StatusCode < 300) / (double)g.Count(), 4),
                    avg_quality = g.Where(r => r.QualityScore.HasValue).Select(r => r.QualityScore!.Value).DefaultIfEmpty(0).Average(),
                    verified_count = g.Count(r => r.ReceiptVerified),
                })
                .ToList();

            var score = scoringEngine.CalculateScore(svc);

            return Results.Ok(new
            {
                service = serviceId,
                current_score = score.Score,
                period_months = period,
                total_ratings = ratings.Count,
                history = dailyScores,
                // x402_price = "0.001 USDC"  // Future: uncomment when x402 is active
            });
        })
        .WithName("GetScoreHistory")
        .WithTags("Premium")
        .Produces(200)
        .Produces(404)
        .WithOpenApi(op =>
        {
            op.Summary = "Get score history for a service (future: 0.001 USDC)";
            op.Description = "Returns daily aggregated rating history for a service over the specified period. Currently free, will require x402 micropayment in the future.";
            return op;
        });

        // --- Score Detailed (future price: 0.001 USDC) ---
        app.MapGet("/v1/score/detailed", async (
            string? did,
            string? service,
            IServiceRepository serviceRepo,
            IRatingRepository ratingRepo,
            IScoringEngine scoringEngine) =>
        {
            var raw = service ?? did;
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { error = "missing_service", message = "Query parameter 'service' (or 'did') is required" });

            var serviceId = ServiceIdentifier.Normalize(raw);
            var svc = await serviceRepo.GetByDidAsync(serviceId);
            if (svc is null)
                return Results.NotFound(new { error = "service_not_found", message = "No ratings found for this service" });

            var ratings = await ratingRepo.GetHistoryAsync(serviceId, 3);
            var score = scoringEngine.CalculateScore(svc);

            var latencies = ratings.Select(r => r.LatencyMs).OrderBy(l => l).ToList();

            return Results.Ok(new
            {
                service = serviceId,
                score = score.Score,
                confidence = score.Confidence,
                ratings_count = score.RatingsCount,
                dimensions = new
                {
                    availability = score.Dimensions.Availability,
                    latency = score.Dimensions.Latency,
                    conformity = score.Dimensions.Conformity,
                },
                latency_percentiles = latencies.Count > 0 ? new
                {
                    p50 = Percentile(latencies, 50),
                    p90 = Percentile(latencies, 90),
                    p95 = Percentile(latencies, 95),
                    p99 = Percentile(latencies, 99),
                } : null,
                quality_distribution = new
                {
                    score_1 = ratings.Count(r => r.QualityScore == 1),
                    score_2 = ratings.Count(r => r.QualityScore == 2),
                    score_3 = ratings.Count(r => r.QualityScore == 3),
                    score_4 = ratings.Count(r => r.QualityScore == 4),
                    score_5 = ratings.Count(r => r.QualityScore == 5),
                    unrated = ratings.Count(r => !r.QualityScore.HasValue),
                },
                receipt_stats = new
                {
                    total = ratings.Count,
                    with_receipt = ratings.Count(r => r.HasReceipt),
                    verified = ratings.Count(r => r.ReceiptVerified),
                },
                service_supports_receipts = score.ServiceSupportsReceipts,
                last_rated = score.LastRatedAt,
                // x402_price = "0.001 USDC"
            });
        })
        .WithName("GetScoreDetailed")
        .WithTags("Premium")
        .Produces(200)
        .Produces(404)
        .WithOpenApi(op =>
        {
            op.Summary = "Get detailed score breakdown (future: 0.001 USDC)";
            op.Description = "Returns detailed analytics: latency percentiles, quality distribution, receipt stats. Currently free, will require x402 micropayment in the future.";
            return op;
        });

        // --- Bulk Scores (future price: 0.05 USDC) ---
        app.MapPost("/v1/scores/bulk", async (
            BulkScoreRequest request,
            IServiceRepository serviceRepo,
            IScoringEngine scoringEngine) =>
        {
            if (request.Dids is null || request.Dids.Count == 0)
                return Results.BadRequest(new { error = "missing_dids", message = "Field 'dids' is required and must not be empty" });

            if (request.Dids.Count > 100)
                return Results.BadRequest(new { error = "too_many_dids", message = "Maximum 100 DIDs per request" });

            // Normalize all DIDs and batch-query in a single DB call
            var normalizedDids = request.Dids.Select(ServiceIdentifier.Normalize).ToList();
            var services = await serviceRepo.GetByDidsAsync(normalizedDids);
            var serviceMap = services.ToDictionary(s => s.Did);

            var foundCount = 0;
            var results = normalizedDids.Select(did =>
            {
                if (!serviceMap.TryGetValue(did, out var svc))
                    return new BulkScoreItem(did, false, null, null, null);

                var score = scoringEngine.CalculateScore(svc);
                foundCount++;
                return new BulkScoreItem(did, true, score.Score, score.Confidence, score.RatingsCount);
            }).ToList();

            return Results.Ok(new
            {
                results = results.Select(r => r.Found
                    ? new { service = r.Service, found = r.Found, score = r.Score, confidence = r.Confidence, ratings_count = r.RatingsCount }
                    : new { service = r.Service, found = r.Found, score = (double?)null, confidence = (double?)null, ratings_count = (int?)null }),
                requested = request.Dids.Count,
                found = foundCount,
            });
        })
        .WithName("BulkScores")
        .WithTags("Premium")
        .Produces(200)
        .Produces(400)
        .WithOpenApi(op =>
        {
            op.Summary = "Get scores for multiple services at once (future: 0.05 USDC)";
            op.Description = "Returns trust scores for up to 100 services in a single request. Currently free, will require x402 micropayment in the future.";
            return op;
        });
    }

    /// <summary>
    /// Nearest-rank percentile method. For p50 on [1,2,3,4,5], returns 3.
    /// </summary>
    private static int Percentile(List<int> sorted, int percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}

public sealed record BulkScoreRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("dids")]
    public List<string>? Dids { get; init; }
}

internal sealed record BulkScoreItem(string Service, bool Found, double? Score, double? Confidence, int? RatingsCount);

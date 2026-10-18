using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class ServicesEndpoints
{
    public static void MapServicesEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/services", async (
            int? limit,
            int? offset,
            string? sort_by,
            string? order,
            double? min_score,
            int? min_ratings,
            string? x402,
            IServiceRepository serviceRepo,
            IScoringEngine scoringEngine) =>
        {
            // Parsed here rather than bound as bool?, which turns a bad value into a 500.
            bool? x402Filter = x402?.ToLowerInvariant() switch
            {
                null => null,
                "true" => true,
                "false" => false,
                _ => default(bool?),
            };
            if (x402 is not null && x402Filter is null)
                return Results.BadRequest(new { error = "invalid_x402", message = "x402 must be true or false" });

            var filter = new ServiceListFilter
            {
                Limit = limit ?? 20,
                Offset = offset ?? 0,
                SortBy = sort_by ?? "score",
                Order = order ?? "desc",
                MinScore = min_score,
                MinRatings = min_ratings,
                X402 = x402Filter,
            };

            // Validate inputs
            if (min_score.HasValue && (min_score < 0 || min_score > 1))
                return Results.BadRequest(new { error = "invalid_min_score", message = "min_score must be between 0 and 1" });
            if (min_ratings.HasValue && min_ratings < 0)
                return Results.BadRequest(new { error = "invalid_min_ratings", message = "min_ratings must be >= 0" });

            var validSortFields = new[] { "score", "ratings_count", "last_rated" };
            if (!validSortFields.Contains(filter.SortBy))
                return Results.BadRequest(new
                {
                    error = "invalid_sort_by",
                    message = $"sort_by must be one of: {string.Join(", ", validSortFields)}"
                });

            var services = await serviceRepo.ListAsync(filter);
            var total = await serviceRepo.CountAsync(filter);
            var results = services.Select(s =>
            {
                var score = scoringEngine.CalculateScore(s);
                return new
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
                    service_supports_receipts = score.ServiceSupportsReceipts,
                    last_rated = score.LastRatedAt,
                };
            });

            return Results.Ok(new
            {
                services = results,
                pagination = new
                {
                    limit = filter.Limit,
                    offset = filter.Offset,
                    count = services.Count,
                    total,
                }
            });
        })
        .WithName("ListServices")
        .WithTags("Services")
        .Produces(200)
        .Produces(400)
        .WithSummary("List rated services")
        .WithDescription("Returns a paginated list of services with their trust scores. " +
            "Use this to discover which services have been rated and find reliable ones. " +
            "pagination.total is the number of services matching the filters across all pages. " +
            "x402=true keeps only the paid x402 endpoints whose payment terms the registry records.");
    }
}

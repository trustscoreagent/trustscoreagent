using Dapper;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Data;

public sealed class ServiceRepository : IServiceRepository
{
    private readonly DbConnectionFactory _db;

    public ServiceRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<ServiceEntity?> GetByDidAsync(string did)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ServiceEntity>(
            """
            SELECT did, alpha, beta,
                   alpha_availability AS AlphaAvailability,
                   beta_availability AS BetaAvailability,
                   alpha_latency AS AlphaLatency,
                   beta_latency AS BetaLatency,
                   alpha_conformity AS AlphaConformity,
                   beta_conformity AS BetaConformity,
                   ratings_count AS RatingsCount,
                   supports_receipts AS SupportsReceipts,
                   last_rated_at AS LastRatedAt,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM services
            WHERE did = @Did
            """,
            new { Did = did });
    }

    public async Task<IReadOnlyList<ServiceEntity>> GetByDidsAsync(IReadOnlyList<string> dids)
    {
        if (dids.Count == 0) return Array.Empty<ServiceEntity>();

        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<ServiceEntity>(
            """
            SELECT did, alpha, beta,
                   alpha_availability AS AlphaAvailability,
                   beta_availability AS BetaAvailability,
                   alpha_latency AS AlphaLatency,
                   beta_latency AS BetaLatency,
                   alpha_conformity AS AlphaConformity,
                   beta_conformity AS BetaConformity,
                   ratings_count AS RatingsCount,
                   supports_receipts AS SupportsReceipts,
                   last_rated_at AS LastRatedAt,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM services
            WHERE did = ANY(@Dids)
            """,
            new { Dids = dids.ToArray() });
        return results.ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<ServiceEntity>> GetByProviderAsync(string provider)
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<ServiceEntity>(
            """
            SELECT did, alpha, beta,
                   alpha_availability AS AlphaAvailability,
                   beta_availability AS BetaAvailability,
                   alpha_latency AS AlphaLatency,
                   beta_latency AS BetaLatency,
                   alpha_conformity AS AlphaConformity,
                   beta_conformity AS BetaConformity,
                   ratings_count AS RatingsCount,
                   supports_receipts AS SupportsReceipts,
                   last_rated_at AS LastRatedAt,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM services
            WHERE did LIKE @Pattern
            ORDER BY ratings_count DESC
            """,
            new { Pattern = $"{provider}/%" });
        return results.ToList().AsReadOnly();
    }

    public async Task UpsertAsync(ServiceEntity service)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO services (did, alpha, beta,
                alpha_availability, beta_availability,
                alpha_latency, beta_latency,
                alpha_conformity, beta_conformity,
                ratings_count, supports_receipts, last_rated_at, created_at, updated_at)
            VALUES (@Did, @Alpha, @Beta,
                @AlphaAvailability, @BetaAvailability,
                @AlphaLatency, @BetaLatency,
                @AlphaConformity, @BetaConformity,
                @RatingsCount, @SupportsReceipts, @LastRatedAt, @CreatedAt, @UpdatedAt)
            ON CONFLICT (did) DO UPDATE SET
                alpha = @Alpha,
                beta = @Beta,
                alpha_availability = @AlphaAvailability,
                beta_availability = @BetaAvailability,
                alpha_latency = @AlphaLatency,
                beta_latency = @BetaLatency,
                alpha_conformity = @AlphaConformity,
                beta_conformity = @BetaConformity,
                ratings_count = @RatingsCount,
                supports_receipts = @SupportsReceipts,
                last_rated_at = @LastRatedAt,
                updated_at = @UpdatedAt
            """,
            service);
    }

    public async Task ApplyRatingAtomicAsync(string did, RatingDelta delta)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO services (did, alpha, beta,
                alpha_availability, beta_availability,
                alpha_latency, beta_latency,
                alpha_conformity, beta_conformity,
                ratings_count, supports_receipts, last_rated_at, created_at, updated_at)
            VALUES (@Did,
                GREATEST(1.0, 1.0 + @AlphaDelta), GREATEST(1.0, 1.0 + @BetaDelta),
                GREATEST(1.0, 1.0 + @AlphaAvailabilityDelta), GREATEST(1.0, 1.0 + @BetaAvailabilityDelta),
                GREATEST(1.0, 1.0 + @AlphaLatencyDelta), GREATEST(1.0, 1.0 + @BetaLatencyDelta),
                GREATEST(1.0, 1.0 + @AlphaConformityDelta), GREATEST(1.0, 1.0 + @BetaConformityDelta),
                1, @SupportsReceipts, NOW(), NOW(), NOW())
            ON CONFLICT (did) DO UPDATE SET
                alpha = GREATEST(1.0, services.alpha * 0.995 + @AlphaDelta),
                beta = GREATEST(1.0, services.beta * 0.995 + @BetaDelta),
                alpha_availability = GREATEST(1.0, services.alpha_availability * 0.995 + @AlphaAvailabilityDelta),
                beta_availability = GREATEST(1.0, services.beta_availability * 0.995 + @BetaAvailabilityDelta),
                alpha_latency = GREATEST(1.0, services.alpha_latency * 0.995 + @AlphaLatencyDelta),
                beta_latency = GREATEST(1.0, services.beta_latency * 0.995 + @BetaLatencyDelta),
                alpha_conformity = GREATEST(1.0, services.alpha_conformity * 0.995 + @AlphaConformityDelta),
                beta_conformity = GREATEST(1.0, services.beta_conformity * 0.995 + @BetaConformityDelta),
                ratings_count = services.ratings_count + 1,
                supports_receipts = services.supports_receipts OR @SupportsReceipts,
                last_rated_at = NOW(),
                updated_at = NOW()
            """,
            new
            {
                Did = did,
                delta.AlphaDelta,
                delta.BetaDelta,
                delta.AlphaAvailabilityDelta,
                delta.BetaAvailabilityDelta,
                delta.AlphaLatencyDelta,
                delta.BetaLatencyDelta,
                delta.AlphaConformityDelta,
                delta.BetaConformityDelta,
                delta.SupportsReceipts,
            });
    }

    public async Task<IReadOnlyList<ServiceEntity>> ListAsync(ServiceListFilter filter)
    {
        using var conn = _db.CreateConnection();

        // Whitelist validation in repository (defense in depth — endpoint also validates)
        var orderColumn = filter.SortBy switch
        {
            "ratings_count" => "ratings_count",
            "last_rated" => "last_rated_at",
            "score" => "(alpha / (alpha + beta))",
            _ => throw new ArgumentException($"Invalid sort field: {filter.SortBy}"),
        };
        var orderDir = filter.Order switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            _ => "DESC",
        };

        var sql = $"""
            SELECT did, alpha, beta,
                   alpha_availability AS AlphaAvailability,
                   beta_availability AS BetaAvailability,
                   alpha_latency AS AlphaLatency,
                   beta_latency AS BetaLatency,
                   alpha_conformity AS AlphaConformity,
                   beta_conformity AS BetaConformity,
                   ratings_count AS RatingsCount,
                   supports_receipts AS SupportsReceipts,
                   last_rated_at AS LastRatedAt,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM services
            WHERE ratings_count >= @MinRatings
              AND (alpha / (alpha + beta)) >= @MinScore
            ORDER BY {orderColumn} {orderDir}
            LIMIT @Limit OFFSET @Offset
            """;

        var results = await conn.QueryAsync<ServiceEntity>(sql, new
        {
            MinRatings = filter.MinRatings ?? 0,
            MinScore = filter.MinScore ?? 0.0,
            Limit = Math.Clamp(filter.Limit, 1, 100),
            Offset = Math.Max(filter.Offset, 0),
        });

        return results.ToList().AsReadOnly();
    }

    public async Task<bool> ExistsAsync(string did)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM services WHERE did = @Did)",
            new { Did = did });
    }
}

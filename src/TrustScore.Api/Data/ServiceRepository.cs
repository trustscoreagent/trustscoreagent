using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Data;

public sealed class ServiceRepository : IServiceRepository
{
    private readonly DbConnectionFactory _db;
    private readonly double _lambda;

    public ServiceRepository(DbConnectionFactory db, IConfiguration config)
    {
        _db = db;
        // Forgetting factor, read from the same config key as the C# scoring engine so the live
        // SQL path and the configured value can't drift (was hard-coded 0.995).
        _lambda = config.GetValue<double>("Scoring:Lambda", 0.995);
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

    // Escape LIKE metacharacters so a provider containing % or _ is matched literally rather than
    // as a wildcard (e.g. "%" must not match every service and pull the whole table into memory).
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
            WHERE did LIKE @Pattern ESCAPE '\'
            ORDER BY ratings_count DESC
            LIMIT 1000
            """,
            new { Pattern = $"{EscapeLike(provider)}/%" });
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

    private const string ApplyRatingSql =
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
            alpha = GREATEST(1.0, services.alpha * @Lambda + @AlphaDelta),
            beta = GREATEST(1.0, services.beta * @Lambda + @BetaDelta),
            alpha_availability = GREATEST(1.0, services.alpha_availability * @Lambda + @AlphaAvailabilityDelta),
            beta_availability = GREATEST(1.0, services.beta_availability * @Lambda + @BetaAvailabilityDelta),
            alpha_latency = GREATEST(1.0, services.alpha_latency * @Lambda + @AlphaLatencyDelta),
            beta_latency = GREATEST(1.0, services.beta_latency * @Lambda + @BetaLatencyDelta),
            alpha_conformity = GREATEST(1.0, services.alpha_conformity * @Lambda + @AlphaConformityDelta),
            beta_conformity = GREATEST(1.0, services.beta_conformity * @Lambda + @BetaConformityDelta),
            ratings_count = services.ratings_count + 1,
            supports_receipts = services.supports_receipts OR @SupportsReceipts,
            last_rated_at = NOW(),
            updated_at = NOW()
        """;

    private object BuildApplyRatingParams(string did, RatingDelta delta) => new
    {
        Did = did,
        Lambda = _lambda,
        delta.AlphaDelta,
        delta.BetaDelta,
        delta.AlphaAvailabilityDelta,
        delta.BetaAvailabilityDelta,
        delta.AlphaLatencyDelta,
        delta.BetaLatencyDelta,
        delta.AlphaConformityDelta,
        delta.BetaConformityDelta,
        delta.SupportsReceipts,
    };

    public async Task ApplyRatingAtomicAsync(string did, RatingDelta delta)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(ApplyRatingSql, BuildApplyRatingParams(did, delta));
    }

    public Task ApplyRatingAtomicAsync(IDbConnection conn, IDbTransaction tx, string did, RatingDelta delta)
        => conn.ExecuteAsync(ApplyRatingSql, BuildApplyRatingParams(did, delta), tx);

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

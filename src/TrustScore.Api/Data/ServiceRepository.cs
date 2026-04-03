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

    public async Task<IReadOnlyList<ServiceEntity>> ListAsync(ServiceListFilter filter)
    {
        using var conn = _db.CreateConnection();

        // Compute score as alpha/(alpha+beta) for sorting
        var orderColumn = filter.SortBy switch
        {
            "ratings_count" => "ratings_count",
            "last_rated" => "last_rated_at",
            _ => "(alpha / (alpha + beta))", // "score" default
        };
        var orderDir = filter.Order == "asc" ? "ASC" : "DESC";

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

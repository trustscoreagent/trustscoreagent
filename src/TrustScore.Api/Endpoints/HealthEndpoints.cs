using TrustScore.Api.Data;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (DbConnectionFactory db, ICacheService cache) =>
        {
            var checks = new Dictionary<string, string>();

            // PostgreSQL is required: the API cannot serve without it.
            var dbOk = false;
            try
            {
                using var conn = db.CreateConnection();
                if (conn is System.Data.Common.DbConnection dbc)
                    await dbc.OpenAsync();
                else
                    conn.Open();
                dbOk = true;
                checks["database"] = "ok";
            }
            catch
            {
                checks["database"] = "error";
            }

            // Redis is optional: the API runs in a degraded mode without it (PostgreSQL fallback),
            // so a Redis outage must not flip the instance to unhealthy and get it recycled.
            var redisOk = await cache.IsAvailableAsync();
            checks["redis"] = redisOk ? "ok" : "error";

            var status = !dbOk ? "unhealthy" : redisOk ? "healthy" : "degraded";

            var response = new
            {
                status,
                checks,
                version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            };

            // Only a database failure is fatal (503); degraded (Redis down) still serves traffic.
            return dbOk
                ? Results.Ok(response)
                : Results.Json(response, statusCode: 503);
        })
        .WithName("Health")
        .WithTags("Infrastructure")
        .ExcludeFromDescription();
    }
}

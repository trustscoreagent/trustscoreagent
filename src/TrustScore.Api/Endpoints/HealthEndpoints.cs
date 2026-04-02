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
            var healthy = true;

            try
            {
                using var conn = db.CreateConnection();
                conn.Open();
                checks["database"] = "ok";
            }
            catch
            {
                checks["database"] = "error";
                healthy = false;
            }

            var redisOk = await cache.IsAvailableAsync();
            checks["redis"] = redisOk ? "ok" : "error";
            if (!redisOk) healthy = false;

            var response = new
            {
                status = healthy ? "healthy" : "unhealthy",
                checks,
                version = "0.1.0",
            };

            return healthy
                ? Results.Ok(response)
                : Results.Json(response, statusCode: 503);
        })
        .WithName("Health")
        .WithTags("Infrastructure")
        .ExcludeFromDescription();
    }
}

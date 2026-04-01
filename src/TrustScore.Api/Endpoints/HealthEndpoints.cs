using StackExchange.Redis;
using TrustScore.Api.Data;

namespace TrustScore.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (DbConnectionFactory db, IConnectionMultiplexer redis) =>
        {
            var checks = new Dictionary<string, string>();
            var healthy = true;

            // Check PostgreSQL
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

            // Check Redis
            try
            {
                var redisDb = redis.GetDatabase();
                await redisDb.PingAsync();
                checks["redis"] = "ok";
            }
            catch
            {
                checks["redis"] = "error";
                healthy = false;
            }

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

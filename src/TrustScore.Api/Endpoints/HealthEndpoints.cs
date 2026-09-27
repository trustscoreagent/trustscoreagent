using TrustScore.Api.Data;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class HealthEndpoints
{
    // InformationalVersion is "<Version>+<commit sha>" when built from git: the release number and
    // the exact commit deployed, instead of the four-part assembly version.
    private static readonly string Informational =
        typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";

    internal static readonly string Version = Informational.Split('+')[0];
    internal static readonly string? Commit = Informational.Contains('+') ? Informational.Split('+')[1] : null;

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
                version = Version,
                commit = Commit,
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

using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Middleware;

/// <summary>
/// Global rate limiting middleware: limits requests per IP across all endpoints.
/// Applied to all routes except /health.
/// </summary>
public sealed class GlobalRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private const int MaxRequestsPerMinute = 120;

    public GlobalRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IRateLimiter rateLimiter)
    {
        // Skip health endpoint
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"global:{ip}";

        var result = await rateLimiter.CheckAsync(key, MaxRequestsPerMinute, TimeSpan.FromMinutes(1));

        context.Response.Headers["X-RateLimit-Limit"] = MaxRequestsPerMinute.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString();

        if (!result.Allowed)
        {
            context.Response.StatusCode = 429;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"rate_limited","message":"Too many requests. Maximum 120 requests per minute."}""");
            return;
        }

        await _next(context);
    }
}

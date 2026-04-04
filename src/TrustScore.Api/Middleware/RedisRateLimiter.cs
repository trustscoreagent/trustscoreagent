using StackExchange.Redis;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Middleware;

public sealed class RedisRateLimiter : IRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRateLimiter> _logger;

    public RedisRateLimiter(IConnectionMultiplexer redis, ILogger<RedisRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckAsync(string key, int maxRequests, TimeSpan window)
    {
        try
        {
            var db = _redis.GetDatabase();
            var redisKey = $"ratelimit:{key}";

            // Atomic increment + set expiry if new key
            var count = await db.StringIncrementAsync(redisKey);

            // Set expiry only on first increment (when count == 1)
            if (count == 1)
                await db.KeyExpireAsync(redisKey, window);

            var allowed = count <= maxRequests;
            return new RateLimitResult(allowed, (int)count, maxRequests);
        }
        catch (Exception ex)
        {
            // Redis down — reject the request (fail closed for security)
            _logger.LogWarning(ex, "Redis rate limiter unavailable, rejecting request for key {Key}", key);
            return new RateLimitResult(false, maxRequests, maxRequests);
        }
    }
}

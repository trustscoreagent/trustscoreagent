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

    // INCR then set the expiry only if the key has none, in a single atomic server-side script.
    // This closes the gap where a crash between INCR and EXPIRE could leave a key with no TTL,
    // permanently blocking an IP/agent. PEXPIRE ... NX requires Redis 7.0+.
    private const string IncrementScript = """
        local count = redis.call('INCR', KEYS[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[1], 'NX')
        return count
        """;

    public async Task<RateLimitResult> CheckAsync(string key, int maxRequests, TimeSpan window)
    {
        try
        {
            var db = _redis.GetDatabase();
            var redisKey = $"ratelimit:{key}";

            var count = (long)await db.ScriptEvaluateAsync(
                IncrementScript,
                new RedisKey[] { redisKey },
                new RedisValue[] { (long)window.TotalMilliseconds });

            var allowed = count <= maxRequests;
            return new RateLimitResult(allowed, (int)Math.Min(count, int.MaxValue), maxRequests);
        }
        catch (Exception ex)
        {
            // Redis down — fail OPEN: per the project convention the API must keep working without
            // Redis, so rate limiting is best-effort and a Redis outage must not take the API down.
            // (Receipt nonce anti-replay stays fail-closed; that lives in RedisCacheService.)
            _logger.LogWarning(ex, "Redis rate limiter unavailable, allowing request for key {Key} (fail-open)", key);
            return new RateLimitResult(true, 0, maxRequests);
        }
    }
}

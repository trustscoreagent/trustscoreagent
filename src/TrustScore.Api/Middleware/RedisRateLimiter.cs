using System.Collections.Concurrent;
using StackExchange.Redis;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Middleware;

public sealed class RedisRateLimiter : IRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRateLimiter> _logger;

    // Per-instance fallback used only while Redis is unreachable, so an outage does not leave the
    // unauthenticated write endpoints completely unlimited. It bounds abuse to maxRequests per
    // window per running instance instead of relying on Redis being up.
    private readonly ConcurrentDictionary<string, InMemoryWindow> _fallback = new();

    public RedisRateLimiter(IConnectionMultiplexer redis, ILogger<RedisRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    // INCR then set the expiry only on the first hit (count == 1), in a single atomic server-side
    // script. Lua scripts run atomically, so INCR and PEXPIRE always execute together — this closes
    // the "key with no TTL" gap without needing PEXPIRE ... NX, which requires Redis 7.0+ (on 6.x
    // that flag raises an error, silently disabling all rate limiting via the fail-open catch).
    private const string IncrementScript = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
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
            // Redis down — fall back to the per-instance limiter rather than failing fully open.
            // The API stays up (project convention: never fail because Redis is down), but the
            // unauthenticated endpoints keep a bound. (Receipt nonce anti-replay stays fail-closed;
            // that lives in RedisCacheService.)
            _logger.LogWarning(ex, "Redis rate limiter unavailable, using in-process fallback for key {Key}", key);
            return CheckInMemory(key, maxRequests, window);
        }
    }

    private RateLimitResult CheckInMemory(string key, int maxRequests, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _fallback.AddOrUpdate(
            key,
            _ => new InMemoryWindow(now + window, 1),
            (_, cur) => now >= cur.ResetAt ? new InMemoryWindow(now + window, 1) : cur with { Count = cur.Count + 1 });

        // Opportunistic prune so the fallback map cannot grow without bound during a long outage.
        if (_fallback.Count > 100_000)
            foreach (var kv in _fallback)
                if (now >= kv.Value.ResetAt)
                    _fallback.TryRemove(kv.Key, out _);

        return new RateLimitResult(entry.Count <= maxRequests, (int)Math.Min(entry.Count, int.MaxValue), maxRequests);
    }

    private sealed record InMemoryWindow(DateTimeOffset ResetAt, int Count);
}

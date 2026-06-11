using StackExchange.Redis;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheService> _logger;

    // Throttle outage warnings so a sustained Redis failure logs roughly once per interval
    // instead of once per request. Stored as ticks for lock-free updates.
    private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);
    private long _lastWarnTicks;

    public RedisCacheService(IConnectionMultiplexer redis, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private void LogOutage(Exception ex, string operation)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastWarnTicks);
        if (now - last < WarnInterval.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastWarnTicks, now, last) == last)
            _logger.LogWarning(ex, "Redis unavailable during {Operation}; serving in degraded mode", operation);
    }

    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.HasValue ? (string?)value : null;
        }
        catch (Exception ex)
        {
            LogOutage(ex, "cache get");
            return null;
        }
    }

    public async Task SetAsync(string key, string value, TimeSpan expiry)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(key, value, expiry);
        }
        catch (Exception ex)
        {
            // Redis unavailable — continue without cache
            LogOutage(ex, "cache set");
        }
    }

    public async Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry)
    {
        try
        {
            var db = _redis.GetDatabase();
            return await db.StringSetAsync(key, value, expiry, when: When.NotExists);
        }
        catch (Exception ex)
        {
            // Redis unavailable — fail closed (assume key exists = reject). This is the receipt
            // nonce anti-replay path: rejecting is the safe choice, unlike the rate limiter.
            LogOutage(ex, "nonce claim");
            return false;
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            // Redis unavailable — cache will expire naturally
            LogOutage(ex, "cache remove");
        }
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.PingAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

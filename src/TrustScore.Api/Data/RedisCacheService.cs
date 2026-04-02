using StackExchange.Redis;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;

    public RedisCacheService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(key);
            return value.HasValue ? (string?)value : null;
        }
        catch
        {
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
        catch
        {
            // Redis unavailable — continue without cache
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
        }
        catch
        {
            // Redis unavailable — cache will expire naturally
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

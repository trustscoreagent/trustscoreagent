using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace TrustScore.Api.Data;

/// <summary>
/// The slice of Redis this deployment owns. Staging and production share one Redis, and without
/// a prefix staging would write cached scores computed from its own database that production then
/// serves, spend production's rate-limit buckets and claim its nonces. Set <see cref="ConfigKey"/>
/// on every deployment but production (which keeps the historical unprefixed keys).
/// </summary>
public sealed class RedisKeyspace
{
    public const string ConfigKey = "Redis:KeyPrefix";

    private readonly IConnectionMultiplexer _redis;
    private readonly string? _prefix;

    public RedisKeyspace(IConnectionMultiplexer redis, IConfiguration configuration)
    {
        _redis = redis;
        _prefix = configuration[ConfigKey] is { Length: > 0 } prefix ? prefix : null;
    }

    public string? Prefix => _prefix;

    /// <summary>A database handle that transparently prefixes every key, including script KEYS.</summary>
    public IDatabase Database() =>
        _prefix is null ? _redis.GetDatabase() : _redis.GetDatabase().WithKeyPrefix(_prefix);
}

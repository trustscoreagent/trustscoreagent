using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TrustScore.Api.Data;
using TrustScore.Api.Middleware;
using TrustScore.Core.Interfaces;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// Finds a reachable Redis for the tests that must run against the real thing: the CI service
/// container (ConnectionStrings__Redis), else a local one on 6379.
/// </summary>
public static class RedisTestServer
{
    public const string SkipReason =
        "No reachable Redis (set ConnectionStrings__Redis or run `docker compose up -d`)";

    public static readonly IConnectionMultiplexer? Connection = Connect();

    private static IConnectionMultiplexer? Connect()
    {
        var target = Environment.GetEnvironmentVariable("ConnectionStrings__Redis") is { Length: > 0 } env
            ? env
            : "localhost:6379";
        try
        {
            var options = ConfigurationOptions.Parse(target);
            options.ConnectTimeout = 3000;
            options.AbortOnConnectFail = true;
            var mux = ConnectionMultiplexer.Connect(options);
            mux.GetDatabase().Ping();
            return mux;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>A [Fact] that only runs when Redis is reachable (always, in CI).</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (RedisTestServer.Connection is null && !TestServices.Required)
            Skip = RedisTestServer.SkipReason;
    }
}

/// <summary>
/// The Redis-backed rate limiter, cache and key prefix against a real Redis. Every other test swaps
/// these for in-memory fakes, so until now the Lua script, SET NX and the staging key prefix had
/// never run against the engine that executes them in production.
/// </summary>
public class RedisIntegrationTests
{
    private static IConnectionMultiplexer Redis =>
        RedisTestServer.Connection
        ?? throw new InvalidOperationException(RedisTestServer.SkipReason + ", and REQUIRE_TEST_SERVICES=1 forbids skipping.");

    // Unique per run, so parallel CI jobs or a shared local Redis never see each other's keys.
    private static readonly string Run = $"test-{Guid.NewGuid():N}:";

    private static RedisKeyspace Keyspace(string? prefix)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RedisKeyspace.ConfigKey] = prefix })
            .Build();
        return new RedisKeyspace(Redis, config);
    }

    private static RedisCacheService Cache(string? prefix) =>
        new(Keyspace(prefix), NullLogger<RedisCacheService>.Instance);

    private static RedisRateLimiter Limiter(string? prefix) =>
        new(Keyspace(prefix), NullLogger<RedisRateLimiter>.Instance);

    [RedisFact]
    public async Task RateLimiter_CountsWithinTheWindow_AndRejectsPastTheLimit()
    {
        var limiter = Limiter(Run);
        var key = $"ip:203.0.113.7:{Guid.NewGuid():N}";

        var results = new List<RateLimitResult>();
        for (var i = 0; i < 4; i++)
            results.Add(await limiter.CheckAsync(key, maxRequests: 3, TimeSpan.FromMinutes(1)));

        results.Select(r => r.Allowed).Should().Equal(true, true, true, false);
    }

    [RedisFact]
    public async Task RateLimiter_StartsAFreshWindowOnceItExpires()
    {
        var limiter = Limiter(Run);
        var key = $"ip:203.0.113.8:{Guid.NewGuid():N}";

        (await limiter.CheckAsync(key, 1, TimeSpan.FromMilliseconds(300))).Allowed.Should().BeTrue();
        (await limiter.CheckAsync(key, 1, TimeSpan.FromMilliseconds(300))).Allowed.Should().BeFalse();
        await Task.Delay(700);
        (await limiter.CheckAsync(key, 1, TimeSpan.FromMilliseconds(300))).Allowed.Should().BeTrue();
    }

    [RedisFact]
    public async Task SetIfNotExists_ClaimsANonceExactlyOnce()
    {
        var cache = Cache(Run);
        var nonce = $"agent-nonce:did:key:z6Mk:{Guid.NewGuid():N}";

        (await cache.SetIfNotExistsAsync(nonce, "used", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await cache.SetIfNotExistsAsync(nonce, "used", TimeSpan.FromMinutes(1))).Should().BeFalse();
        (await cache.IsAvailableAsync()).Should().BeTrue();
    }

    [RedisFact]
    public async Task Cache_SetGetRemove_RoundTrips()
    {
        var cache = Cache(Run);
        var key = $"score:{Guid.NewGuid():N}";

        await cache.SetAsync(key, "{\"score\":0.9}", TimeSpan.FromMinutes(1));
        (await cache.GetAsync(key)).Should().Be("{\"score\":0.9}");
        await cache.RemoveAsync(key);
        (await cache.GetAsync(key)).Should().BeNull();
    }

    [RedisFact]
    public async Task KeyPrefix_KeepsStagingAndProductionApart()
    {
        // Staging and production share one Redis. With the prefix, staging's cached score, rate
        // limit bucket and nonce must be invisible to production, and stored under the prefix.
        var staging = Cache(Run + "staging:");
        var production = Cache(Run);
        var key = $"score:{Guid.NewGuid():N}";

        await staging.SetAsync(key, "staging-value", TimeSpan.FromMinutes(1));

        (await production.GetAsync(key)).Should().BeNull();
        (await staging.GetAsync(key)).Should().Be("staging-value");
        ((string?)await Redis.GetDatabase().StringGetAsync($"{Run}staging:{key}")).Should().Be("staging-value");

        var bucket = $"ip:203.0.113.9:{Guid.NewGuid():N}";
        await Limiter(Run + "staging:").CheckAsync(bucket, 1, TimeSpan.FromMinutes(1));
        (await Limiter(Run).CheckAsync(bucket, 1, TimeSpan.FromMinutes(1))).Allowed
            .Should().BeTrue("production's bucket is untouched by staging's traffic");

        var nonce = $"agent-nonce:{Guid.NewGuid():N}";
        (await staging.SetIfNotExistsAsync(nonce, "used", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await production.SetIfNotExistsAsync(nonce, "used", TimeSpan.FromMinutes(1)))
            .Should().BeTrue("a nonce claimed on staging must not be burnt on production");
    }
}

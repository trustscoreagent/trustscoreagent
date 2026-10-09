using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TrustScore.Api.Data;
using TrustScore.Api.Scoring;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>The Redis usage counters behind /v1/stats, against a real Redis.</summary>
public class RedisUsageCounterTests
{
    private static IConnectionMultiplexer Redis =>
        RedisTestServer.Connection
        ?? throw new InvalidOperationException(RedisTestServer.SkipReason + ", and REQUIRE_TEST_SERVICES=1 forbids skipping.");

    private static RedisUsageCounter Counter(string prefix)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RedisKeyspace.ConfigKey] = prefix })
            .Build();
        return new RedisUsageCounter(new RedisKeyspace(Redis, config), NullLogger<RedisUsageCounter>.Instance);
    }

    [RedisFact]
    public async Task Counts_AreSummedOverTheDays_AndExpire()
    {
        var prefix = $"test-{Guid.NewGuid():N}:";
        var counter = Counter(prefix);
        var today = new DateOnly(2031, 3, 10);

        await counter.RecordAsync(today.AddDays(-1), "score", "mcp/0.2.5");
        await counter.RecordAsync(today, "score", "mcp/0.2.5");
        await counter.RecordAsync(today, "score", "browser");
        await counter.RecordAsync(today, "rate", "mcp/0.2.5");
        await counter.RecordAsync(today.AddDays(-5), "score", "mcp/0.2.5"); // outside the window

        var counts = await counter.ReadAsync(today.AddDays(-1), today);

        counts.Should().BeEquivalentTo(new[]
        {
            new TrustScore.Core.Interfaces.UsageCount("score", "mcp/0.2.5", 2),
            new TrustScore.Core.Interfaces.UsageCount("score", "browser", 1),
            new TrustScore.Core.Interfaces.UsageCount("rate", "mcp/0.2.5", 1),
        });

        var ttl = await Redis.GetDatabase().KeyTimeToLiveAsync($"{prefix}usage:20310310");
        ttl.Should().NotBeNull("counters are kept for a bounded time");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.FromDays(99));
    }
}

/// <summary>The ratings half of /v1/stats, against a real migrated PostgreSQL database.</summary>
public class UsageStatsRepositoryTests : PostgresDatabaseTest
{
    private const string ProbeDid = "did:web:trustscoreagent.com:probe";

    [PostgresFact]
    public async Task RatingStats_SeparateTheProbeFromOtherAgents()
    {
        var now = DateTimeOffset.UtcNow;
        Rating Make(string agent, DateTimeOffset at, bool signed = false, bool receipt = false) => new()
        {
            ServiceDid = "svc-stats.test/api",
            AgentDid = agent,
            Metrics = new RatingMetrics { StatusCode = 200, LatencyMs = 90, SchemaValid = true },
            SignatureVerified = signed,
            HasReceipt = receipt,
            ReceiptVerified = receipt,
            CreatedAt = at,
        };

        var writer = new TransactionalRatingWriter(
            Db, new ServiceRepository(Db, new ConfigurationBuilder().Build()), new RatingRepository(Db));
        var engine = new BetaReputationSystem();
        foreach (var rating in new[]
                 {
                     Make(ProbeDid, now.AddHours(-1)),
                     Make(ProbeDid, now.AddHours(-2)),
                     Make("did:web:agent-a.test", now.AddHours(-3), signed: true),
                     Make("did:web:agent-a.test", now.AddHours(-4), receipt: true),
                     Make("did:web:agent-b.test", now.AddHours(-5), signed: true),
                     Make("did:web:agent-c.test", now.AddDays(-30)), // before the window
                 })
        {
            await writer.SubmitAsync(rating.ServiceDid, engine.ComputeDelta(rating), rating);
        }

        // created_at is set by the writer as given; make sure the old one really is old.
        using (var conn = Db.CreateConnection())
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM ratings WHERE created_at < NOW() - INTERVAL '7 days'")).Should().Be(1);

        var stats = await new UsageStatsRepository(Db).GetRatingStatsAsync(now.AddDays(-7), ProbeDid);

        stats.Should().Be(new TrustScore.Core.Interfaces.RatingStats(
            Total: 5, FromProbe: 2, FromAgents: 3, DistinctAgents: 2, Signed: 2, WithVerifiedReceipt: 1));
    }
}

using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TrustScore.Api.Data;
using TrustScore.Api.Scoring;
using TrustScore.Core.Audit;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// Exercises the PRODUCTION SQL paths against a real, fully-migrated PostgreSQL database.
/// Until now every integration test replaced the repositories with in-memory fakes, so
/// ApplyRatingSql, the transactional write, the Merkle leaf ordering and the LIKE escaping had
/// never run against the engine that executes them in prod (2026-07 audit, test debt).
/// </summary>
public class PostgresRepositoryTests : PostgresDatabaseTest
{
    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    private ServiceRepository Services() => new(Db, EmptyConfig());
    private RatingRepository Ratings() => new(Db);

    private static Rating MakeRating(
        string serviceDid, string agentDid = "did:web:agent-a.test",
        int statusCode = 200, int latencyMs = 100, bool? schemaValid = true,
        double weight = 1.0, bool receiptVerified = false, DateTimeOffset? createdAt = null) => new()
        {
            ServiceDid = serviceDid,
            AgentDid = agentDid,
            Metrics = new RatingMetrics { StatusCode = statusCode, LatencyMs = latencyMs, SchemaValid = schemaValid },
            Weight = weight,
            HasReceipt = receiptVerified,
            ReceiptVerified = receiptVerified,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

    // --- ApplyRatingSql: the live scoring path (the C# ApplyRating is not what prod runs) ---

    [PostgresFact]
    public async Task ApplyRatingSql_FirstRating_StartsFromTheBetaPrior()
    {
        var repo = Services();
        var engine = new BetaReputationSystem();
        var delta = engine.ComputeDelta(MakeRating("apply-first.test/api"));

        await repo.ApplyRatingAtomicAsync("apply-first.test/api", delta);

        var svc = await repo.GetByDidAsync("apply-first.test/api");
        svc.Should().NotBeNull();
        // Beta(1,1) prior + one fully positive rating of weight 1 on all three dimensions.
        svc!.Alpha.Should().BeApproximately(4.0, 1e-9);
        svc.Beta.Should().BeApproximately(1.0, 1e-9);
        svc.AlphaAvailability.Should().BeApproximately(2.0, 1e-9);
        svc.AlphaLatency.Should().BeApproximately(2.0, 1e-9);
        svc.AlphaConformity.Should().BeApproximately(2.0, 1e-9);
        svc.RatingsCount.Should().Be(1);
        svc.SupportsReceipts.Should().BeFalse();
    }

    [PostgresFact]
    public async Task ApplyRatingSql_SecondRating_AppliesForgettingFactor_AndFloorsAtOne()
    {
        var repo = Services();
        var engine = new BetaReputationSystem();
        var positive = engine.ComputeDelta(MakeRating("apply-decay.test/api"));

        await repo.ApplyRatingAtomicAsync("apply-decay.test/api", positive);
        await repo.ApplyRatingAtomicAsync("apply-decay.test/api", positive);

        var svc = await repo.GetByDidAsync("apply-decay.test/api");
        // alpha = max(1, 4·λ + 3) with λ = 0.995 ; beta decays below 1 and must be floored.
        svc!.Alpha.Should().BeApproximately(4.0 * 0.995 + 3.0, 1e-9);
        svc.Beta.Should().BeApproximately(1.0, 1e-9, "the Beta parameter is floored at the prior");
        svc.AlphaAvailability.Should().BeApproximately(2.0 * 0.995 + 1.0, 1e-9);
        svc.RatingsCount.Should().Be(2);
    }

    [PostgresFact]
    public async Task ApplyRatingSql_SupportsReceipts_IsSticky()
    {
        var repo = Services();
        var engine = new BetaReputationSystem();

        await repo.ApplyRatingAtomicAsync("receipts-or.test/api",
            engine.ComputeDelta(MakeRating("receipts-or.test/api", receiptVerified: true)));
        await repo.ApplyRatingAtomicAsync("receipts-or.test/api",
            engine.ComputeDelta(MakeRating("receipts-or.test/api", receiptVerified: false)));

        var svc = await repo.GetByDidAsync("receipts-or.test/api");
        svc!.SupportsReceipts.Should().BeTrue("one verified receipt proves the service emits receipts");
    }

    [PostgresFact]
    public async Task ApplyRatingSql_NegativeRating_PenalizesWithoutTouchingPositiveDimensions()
    {
        var repo = Services();
        var engine = new BetaReputationSystem();
        var negative = engine.ComputeDelta(MakeRating(
            "apply-neg.test/api", statusCode: 500, latencyMs: 5000, schemaValid: false));

        await repo.ApplyRatingAtomicAsync("apply-neg.test/api", negative);

        var svc = await repo.GetByDidAsync("apply-neg.test/api");
        svc!.Alpha.Should().BeApproximately(1.0, 1e-9);
        svc.Beta.Should().BeApproximately(4.0, 1e-9);
        svc.BetaAvailability.Should().BeApproximately(2.0, 1e-9);
        svc.BetaLatency.Should().BeApproximately(2.0, 1e-9);
        svc.BetaConformity.Should().BeApproximately(2.0, 1e-9);
    }

    // --- Transactional write + Merkle leaf round-trip (M7) ---

    [PostgresFact]
    public async Task TransactionalWriter_PersistsServiceAndRating_AndTheStoredLeafHashSurvivesThePostgresRoundTrip()
    {
        var services = Services();
        var ratings = Ratings();
        var writer = new TransactionalRatingWriter(Db, services, ratings);
        var engine = new BetaReputationSystem();

        // Force sub-microsecond ticks: .NET is 100 ns-precise, PostgreSQL TIMESTAMPTZ is µs-precise.
        // The stored merkle_leaf_hash must be computed on the value PostgreSQL will give back,
        // otherwise an external verifier reading the DB concludes the leaf was falsified.
        var createdAt = new DateTimeOffset(
            DateTimeOffset.UtcNow.Ticks / 10 * 10 + 7, TimeSpan.Zero);
        var rating = MakeRating("txn-write.test/api", createdAt: createdAt);

        await writer.SubmitAsync("txn-write.test/api", engine.ComputeDelta(rating), rating);

        var svc = await services.GetByDidAsync("txn-write.test/api");
        svc!.RatingsCount.Should().Be(1);

        var leaf = await ratings.GetLeafInfoAsync(rating.Id);
        leaf.Should().NotBeNull();
        var recomputed = Convert.ToHexString(
            MerkleTree.ComputeLeafHash(leaf!.Id, leaf.ServiceDid, leaf.CreatedAt)).ToLowerInvariant();
        leaf.MerkleLeafHash.Should().Be(recomputed,
            "the stored leaf hash must match a hash recomputed from the re-read row");
    }

    // --- Anchored leaf set: cutoff filter and deterministic order (H2) ---

    [PostgresFact]
    public async Task GetLeafHashesUpTo_FiltersByCutoff_InDeterministicOrder()
    {
        var services = Services();
        var ratings = Ratings();
        var writer = new TransactionalRatingWriter(Db, services, ratings);
        var engine = new BetaReputationSystem();
        var now = DateTimeOffset.UtcNow;

        var old1 = MakeRating("leaves.test/api", createdAt: now.AddMinutes(-10));
        var old2 = MakeRating("leaves.test/api", createdAt: now.AddMinutes(-8));
        var old3 = MakeRating("leaves.test/api", createdAt: now.AddMinutes(-6));
        var fresh = MakeRating("leaves.test/api", createdAt: now);
        // Insert out of created_at order on purpose.
        foreach (var r in new[] { old2, fresh, old3, old1 })
            await writer.SubmitAsync(r.ServiceDid, engine.ComputeDelta(r), r);

        var anchored = await ratings.GetLeafHashesUpToAsync(now.AddMinutes(-5));

        anchored.Select(l => l.Id).Should().Equal(old1.Id, old2.Id, old3.Id);
        anchored.Should().BeInAscendingOrder(l => l.CreatedAt);

        // The set is reproducible: a second read returns the identical sequence.
        var again = await ratings.GetLeafHashesUpToAsync(now.AddMinutes(-5));
        again.Select(l => l.Id).Should().Equal(anchored.Select(l => l.Id));
    }

    // --- LIKE escaping (M5) ---

    [PostgresFact]
    public async Task GetByProvider_TreatsLikeWildcardsLiterally()
    {
        var repo = Services();
        var engine = new BetaReputationSystem();
        var delta = engine.ComputeDelta(MakeRating("p_x.test/a"));
        await repo.ApplyRatingAtomicAsync("p_x.test/a", delta);
        await repo.ApplyRatingAtomicAsync("pyx.test/b", delta);

        // Unescaped, LIKE 'p_x.test/%' would also match pyx.test/b.
        var results = await repo.GetByProviderAsync("p_x.test");
        results.Select(s => s.Did).Should().Equal("p_x.test/a");

        // Unescaped, '%' would match — and load — the entire table.
        (await repo.GetByProviderAsync("%")).Should().BeEmpty();
    }

    // --- Agent trust upsert (UNNEST batch) ---

    [PostgresFact]
    public async Task AgentTrustUpsert_InsertsThenUpdates_InOneBatch()
    {
        var repo = new AgentRepository(Db, new FakeCacheService());
        await repo.UpsertTrustScoresAsync(new Dictionary<string, double>
        {
            ["did:web:agent-a.test"] = 0.7,
            ["did:web:agent-b.test"] = 0.2,
        });
        await repo.UpsertTrustScoresAsync(new Dictionary<string, double>
        {
            ["did:web:agent-a.test"] = 0.9,
        });

        // Fresh repository + cache so the values are read from PostgreSQL, not the cache.
        var fresh = new AgentRepository(Db, new FakeCacheService());
        (await fresh.GetTrustScoreAsync("did:web:agent-a.test")).Should().BeApproximately(0.9, 1e-9);
        (await fresh.GetTrustScoreAsync("did:web:agent-b.test")).Should().BeApproximately(0.2, 1e-9);
        (await fresh.GetTrustScoreAsync("did:web:agent-unknown.test")).Should().Be(0.5);
    }

    // --- SQL aggregates ---

    [PostgresFact]
    public async Task DailyHistory_AggregatesInSql()
    {
        var services = Services();
        var ratings = Ratings();
        var writer = new TransactionalRatingWriter(Db, services, ratings);
        var engine = new BetaReputationSystem();

        var r1 = MakeRating("daily.test/api", latencyMs: 100);
        var r2 = MakeRating("daily.test/api", latencyMs: 300, statusCode: 500, schemaValid: false);
        await writer.SubmitAsync(r1.ServiceDid, engine.ComputeDelta(r1), r1);
        await writer.SubmitAsync(r2.ServiceDid, engine.ComputeDelta(r2), r2);

        var history = await ratings.GetDailyHistoryAsync("daily.test/api", months: 1);

        history.Should().HaveCount(1);
        history[0].RatingsCount.Should().Be(2);
        history[0].AvgLatencyMs.Should().Be(200);
        history[0].SuccessRate.Should().BeApproximately(0.5, 1e-9);
    }

    // --- Rate-limit window count ---

    [PostgresFact]
    public async Task CountRecent_OnlyCountsTheAgentServicePair_InsideTheWindow()
    {
        var services = Services();
        var ratings = Ratings();
        var writer = new TransactionalRatingWriter(Db, services, ratings);
        var engine = new BetaReputationSystem();

        var inWindow = MakeRating("count.test/api", agentDid: "did:web:counted.test");
        var otherAgent = MakeRating("count.test/api", agentDid: "did:web:other.test");
        var tooOld = MakeRating("count.test/api", agentDid: "did:web:counted.test",
            createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        foreach (var r in new[] { inWindow, otherAgent, tooOld })
            await writer.SubmitAsync(r.ServiceDid, engine.ComputeDelta(r), r);

        var count = await ratings.CountRecentAsync("did:web:counted.test", "count.test/api", TimeSpan.FromHours(1));

        count.Should().Be(1);
    }
}

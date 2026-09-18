using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TrustScore.Api.Jobs;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// Exercises SeedProber.RunAsync through a stubbed HTTP transport — the probe → measure →
/// score → submit pipeline that was previously untested (2026-07 audit, M11): a transport
/// failure must record 503 with the full timeout as latency (never rewarding a fast refusal)
/// and schema_valid = null (unknown, not false).
/// </summary>
public class SeedProberRunTests
{
    private const string ProbeDid = "did:web:trustscoreagent.com:probe";
    // Above the 2000 ms latency threshold, so a probe timeout is a latency FAILURE — as in prod,
    // where the default timeout is 10 s. (With a timeout ≤ the threshold, a dead target would
    // still collect latency points; see the M11 test below.)
    private const int TimeoutSeconds = 3;

    private sealed class CapturingRatingWriter : IRatingWriter
    {
        public List<(string ServiceId, RatingDelta Delta, Rating Rating)> Submissions { get; } = new();

        public Task SubmitAsync(string serviceId, RatingDelta delta, Rating rating)
        {
            Submissions.Add((serviceId, delta, rating));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryProbeHealth : IProbeHealthRepository
    {
        public Dictionary<string, ProbeTargetHealth> Store { get; } = new();

        public Task<IReadOnlyDictionary<string, ProbeTargetHealth>> GetAllAsync() =>
            Task.FromResult<IReadOnlyDictionary<string, ProbeTargetHealth>>(
                new Dictionary<string, ProbeTargetHealth>(Store));

        public Task UpsertAsync(ProbeTargetHealth health)
        {
            Store[health.ServiceDid] = health;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTrustAgentRepository : IAgentRepository
    {
        private readonly double _trust;
        public FixedTrustAgentRepository(double trust) => _trust = trust;
        public Task<double> GetTrustScoreAsync(string agentDid) => Task.FromResult(_trust);
        public Task UpsertTrustScoresAsync(Dictionary<string, double> scores) => Task.CompletedTask;
    }

    private static async Task<CapturingRatingWriter> RunProbeAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        List<SeedProbeTarget> targets,
        double agentTrust = 0.8)
        => (await RunProbeWithHealthAsync(respond, targets, agentTrust)).Writer;

    private static async Task<(CapturingRatingWriter Writer, InMemoryProbeHealth Health)> RunProbeWithHealthAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        List<SeedProbeTarget> targets,
        double agentTrust = 0.8,
        InMemoryProbeHealth? health = null,
        int quarantineAfter = 12)
    {
        var writer = new CapturingRatingWriter();
        var store = health ?? new InMemoryProbeHealth();
        var prober = new SeedProber(
            new StubHttpClientFactory(new StubHttpMessageHandler(respond)),
            new BetaReputationSystem(),
            writer,
            new FixedTrustAgentRepository(agentTrust),
            store,
            Options.Create(new SeedProbeOptions
            {
                Enabled = true,
                AgentDid = ProbeDid,
                TimeoutSeconds = TimeoutSeconds,
                Targets = targets,
                QuarantineAfterConsecutiveFailures = quarantineAfter,
            }),
            NullLogger<SeedProber>.Instance);

        await prober.RunAsync();
        return (writer, store);
    }

    [Fact]
    public async Task HealthyTarget_RecordsRealMeasurement_WithScaledUnverifiedWeight()
    {
        var writer = await RunProbeAsync(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"current":{"temperature_2m":7.1}}""", Encoding.UTF8, "application/json"),
            },
            new List<SeedProbeTarget>
            {
                new() { Service = "probe-ok.example.com", Url = "https://probe-ok.example.com/v1", ExpectField = "current" },
            },
            agentTrust: 0.8);

        writer.Submissions.Should().HaveCount(1);
        var (serviceId, _, rating) = writer.Submissions[0];

        serviceId.Should().Be("probe-ok.example.com");
        rating.AgentDid.Should().Be(ProbeDid);
        rating.Metrics.StatusCode.Should().Be(200);
        rating.Metrics.SchemaValid.Should().BeTrue();
        rating.Metrics.LatencyMs.Should().BeInRange(1, TimeoutSeconds * 1000);
        rating.HasReceipt.Should().BeFalse();
        rating.ReceiptVerified.Should().BeFalse();
        // Normal unverified weight (0.3) scaled by the probe's own EigenTrust score — no privilege.
        rating.Weight.Should().BeApproximately(0.3 * 0.8, 1e-9);
    }

    [Fact]
    public async Task TransportFailure_RecordsTimeoutLatency_AndUnknownConformity()
    {
        // M11: a connection refused in ~1 ms is an availability failure, not a latency success.
        // The latency must be the full timeout (so AlphaLatency is NOT credited) and schema_valid
        // must be null (nothing was measured), not false (which would wrongly punish conformity).
        var writer = await RunProbeAsync(
            _ => throw new HttpRequestException("connection refused"),
            new List<SeedProbeTarget>
            {
                new() { Service = "probe-dead.example.com", Url = "https://probe-dead.example.com/v1" },
            });

        writer.Submissions.Should().HaveCount(1);
        var (_, delta, rating) = writer.Submissions[0];

        rating.Metrics.StatusCode.Should().Be(503);
        rating.Metrics.LatencyMs.Should().Be(TimeoutSeconds * 1000);
        rating.Metrics.SchemaValid.Should().BeNull();

        // The dead service must gain NO positive scoring dimension from its own failure.
        delta.AlphaAvailabilityDelta.Should().Be(0);
        delta.AlphaConformityDelta.Should().Be(0);
        delta.BetaConformityDelta.Should().Be(0, "an unmeasured schema must not penalize conformity either");
        delta.BetaAvailabilityDelta.Should().BeGreaterThan(0);
        // The reported latency is the full timeout (3000 ms > 2000 ms threshold), so the failure
        // is a latency penalty — a 1 ms connection-refused must never earn a latency point.
        delta.AlphaLatencyDelta.Should().Be(0);
        delta.BetaLatencyDelta.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task NonConformantBody_PenalizesConformity_NotAvailability()
    {
        var writer = await RunProbeAsync(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"unexpected":1}""", Encoding.UTF8, "application/json"),
            },
            new List<SeedProbeTarget>
            {
                new() { Service = "probe-drift.example.com", Url = "https://probe-drift.example.com/v1", ExpectField = "current" },
            });

        var (_, delta, rating) = writer.Submissions.Single();

        rating.Metrics.SchemaValid.Should().BeFalse();
        delta.BetaConformityDelta.Should().BeGreaterThan(0);
        delta.AlphaAvailabilityDelta.Should().BeGreaterThan(0, "the service did respond 200");
    }

    [Fact]
    public async Task DisabledProbe_SubmitsNothing()
    {
        var writer = new CapturingRatingWriter();
        var prober = new SeedProber(
            new StubHttpClientFactory(new StubHttpMessageHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK))),
            new BetaReputationSystem(),
            writer,
            new FixedTrustAgentRepository(0.8),
            new InMemoryProbeHealth(),
            Options.Create(new SeedProbeOptions { Enabled = false }),
            NullLogger<SeedProber>.Instance);

        await prober.RunAsync();

        writer.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task OneFailingTarget_DoesNotStopTheOthers()
    {
        var writer = await RunProbeAsync(
            request => request.RequestUri!.Host == "probe-dead.example.com"
                ? throw new HttpRequestException("unreachable")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
                },
            new List<SeedProbeTarget>
            {
                new() { Service = "probe-dead.example.com", Url = "https://probe-dead.example.com/v1" },
                new() { Service = "probe-ok.example.com", Url = "https://probe-ok.example.com/v1" },
            });

        writer.Submissions.Should().HaveCount(2);
        writer.Submissions.Select(s => s.Rating.Metrics.StatusCode).Should().BeEquivalentTo(new[] { 503, 200 });
    }

    // --- quarantine: a rotted probe URL must not be published as a service outage ---

    private static List<SeedProbeTarget> OneTarget(string host = "probe-moved.example.com") =>
        new() { new() { Service = host, Url = $"https://{host}/v1" } };

    private static readonly Func<HttpRequestMessage, HttpResponseMessage> AlwaysNotFound =
        _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    [Fact]
    public async Task ShortOutage_IsStillRecorded()
    {
        // Below the threshold the failure is treated as evidence about the service, which is the
        // whole point of the probe. Quarantine must not swallow real outages.
        var (writer, health) = await RunProbeWithHealthAsync(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            OneTarget(), quarantineAfter: 3);

        writer.Submissions.Should().ContainSingle();
        health.Store.Values.Single().IsQuarantined.Should().BeFalse();
    }

    [Fact]
    public async Task PersistentFailure_QuarantinesAndStopsRecording()
    {
        // A URL answering 404 forever is our configuration being wrong, not the service failing.
        var health = new InMemoryProbeHealth();
        var targets = OneTarget();

        for (var pass = 1; pass <= 3; pass++)
            await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);

        health.Store.Values.Single().IsQuarantined.Should().BeTrue();

        // A fourth pass, once quarantined, must publish nothing at all.
        var (writer, _) = await RunProbeWithHealthAsync(
            AlwaysNotFound, targets, health: health, quarantineAfter: 3);

        writer.Submissions.Should().BeEmpty("a quarantined target must not accuse the service");
    }

    [Fact]
    public async Task QuarantinedTarget_RecoversOnceItAnswersAgain()
    {
        // Still probed every pass, so a target whose URL comes back to life heals by itself
        // without anyone editing config.
        var health = new InMemoryProbeHealth();
        var targets = OneTarget();

        for (var pass = 1; pass <= 3; pass++)
            await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);
        health.Store.Values.Single().IsQuarantined.Should().BeTrue();

        var (writer, after) = await RunProbeWithHealthAsync(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") },
            targets, health: health, quarantineAfter: 3);

        after.Store.Values.Single().IsQuarantined.Should().BeFalse();
        after.Store.Values.Single().ConsecutiveFailures.Should().Be(0);
        writer.Submissions.Should().ContainSingle("recording resumes as soon as the URL answers");
    }

    [Fact]
    public async Task OneSuccess_ResetsTheFailureStreak()
    {
        // Intermittent failures must never accumulate into a quarantine: only an unbroken run of
        // failures is evidence that the URL itself is wrong.
        var health = new InMemoryProbeHealth();
        var targets = OneTarget();

        await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);
        await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);
        await RunProbeWithHealthAsync(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") },
            targets, health: health, quarantineAfter: 3);
        await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);

        var entry = health.Store.Values.Single();
        entry.IsQuarantined.Should().BeFalse();
        entry.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task QuarantineTimestamp_DoesNotMoveOnLaterFailures()
    {
        // It records when the target stopped being trusted, so it stays useful for triage.
        var health = new InMemoryProbeHealth();
        var targets = OneTarget();

        for (var pass = 1; pass <= 3; pass++)
            await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);
        var stamped = health.Store.Values.Single().QuarantinedAt;

        await RunProbeWithHealthAsync(AlwaysNotFound, targets, health: health, quarantineAfter: 3);

        health.Store.Values.Single().QuarantinedAt.Should().Be(stamped);
    }
}

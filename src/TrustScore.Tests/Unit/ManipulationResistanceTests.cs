using System.Net;
using FluentAssertions;
using TrustScore.Api.Endpoints;
using TrustScore.Api.Jobs;
using TrustScore.Api.Middleware;
using TrustScore.Api.Scoring;
using TrustScore.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace TrustScore.Tests.Unit;

/// <summary>
/// Replays the score attacks from the 2026-10 audit against the scoring model: the same weight
/// function as POST /v1/rate (RatingWeight), the same per-source repeat and newcomer rules, and the
/// same time-based forgetting as the SQL update (BetaReputationSystem.ApplyRating mirrors it).
/// The service under attack has 100 days of probe history, as the endpoints the probe measures do.
/// Before these rules, one unsigned address took such a service from 0.96 to 0.43 in 24 hours.
/// </summary>
public class ManipulationResistanceTests
{
    private readonly ITestOutputHelper _output;
    private readonly BetaReputationSystem _scoring = new();
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const double ProbeWeight = SeedProber.SeedProbeBaseWeight * EigenTrustEngine.PreTrustedScore;

    public ManipulationResistanceTests(ITestOutputHelper output) => _output = output;

    private static Rating Observation(bool works, double weight) => new()
    {
        ServiceDid = "victim.example",
        AgentDid = "did:key:zAny",
        Metrics = works
            ? new RatingMetrics { StatusCode = 200, LatencyMs = 120, SchemaValid = true }
            : new RatingMetrics { StatusCode = 500, LatencyMs = 9000, SchemaValid = false },
        Weight = weight,
    };

    /// <summary>A healthy service the probe has measured four times a day for 100 days.</summary>
    private (ServiceEntity Service, DateTimeOffset Now) ProbedFor(int days)
    {
        var service = new ServiceEntity { Did = "victim.example" };
        var now = Start;
        for (var i = 0; i < days * 4; i++)
        {
            service = _scoring.ApplyRating(service, Observation(works: true, ProbeWeight), now);
            now = now.AddHours(6);
        }
        return (service, now);
    }

    /// <summary>
    /// <paramref name="sources"/> distinct addresses each send the per-address quota of failure
    /// reports every hour (10, the limit), with fresh keys, for <paramref name="hours"/>, while the
    /// probe keeps measuring. Returns the score at the end.
    /// </summary>
    private double Attack(ServiceEntity service, DateTimeOffset now, int sources, int hours, bool signed, double trust)
    {
        const int perSourcePerHour = 10;
        var repeats = new int[sources];
        var newcomersToday = 0;

        for (var hour = 0; hour < hours; hour++)
        {
            if (hour % 24 == 0)
            {
                Array.Clear(repeats);
                newcomersToday = 0;
            }
            if (hour % 6 == 0)
                service = _scoring.ApplyRating(service, Observation(works: true, ProbeWeight), now);

            for (var r = 0; r < perSourcePerHour; r++)
                for (var s = 0; s < sources; s++)
                {
                    var repeat = ++repeats[s];
                    var exhausted = trust <= EigenTrustEngine.DefaultTrustScore
                                    && ++newcomersToday > RateEndpoints.NewcomerRatingsPerProviderPerDay;
                    var weight = RatingWeight.Counted(receiptVerified: false, signed, trust, repeat, exhausted);
                    service = _scoring.ApplyRating(service, Observation(works: false, weight), now);
                }

            now = now.AddHours(1);
        }

        return _scoring.CalculateScore(service).Score;
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(1000, 24)]
    public void NewIdentities_For24Hours_BarelyMoveAProbedService(int sources, int hours)
    {
        var (service, now) = ProbedFor(100);
        var before = _scoring.CalculateScore(service).Score;

        var after = Attack(service, now, sources, hours, signed: true, trust: EigenTrustEngine.DefaultTrustScore);

        _output.WriteLine($"{sources} address(es), {hours} h: {before:F4} -> {after:F4}");
        (before - after).Should().BeLessThan(0.01);
    }

    [Fact]
    public void AMonthOfAThousandAddresses_StaysBounded()
    {
        // The newcomers' allowance caps what any crowd of new identities adds per day, however
        // large: this is the most a sustained, distributed attack can do.
        var (service, now) = ProbedFor(100);
        var before = _scoring.CalculateScore(service).Score;

        var after = Attack(service, now, sources: 1000, hours: 30 * 24, signed: true, trust: EigenTrustEngine.DefaultTrustScore);

        _output.WriteLine($"1000 addresses, 30 days: {before:F4} -> {after:F4}");
        after.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void UnsignedNewcomers_WeighHalfOfSignedOnes()
    {
        RatingWeight.Counted(false, signed: false, EigenTrustEngine.DefaultTrustScore, 1, false)
            .Should().Be(RatingWeight.Counted(false, signed: true, EigenTrustEngine.DefaultTrustScore, 1, false) / 2);
    }

    [Fact]
    public void ASelfIssuedReceipt_GivesANewcomerNoExtraWeight()
    {
        // A service can sign receipts for its own calls; from a key with no earned trust that is
        // no evidence of independence, so it weighs what a signed rating without a receipt weighs.
        RatingWeight.Counted(receiptVerified: true, signed: true, EigenTrustEngine.DefaultTrustScore, 1, false)
            .Should().Be(RatingWeight.Counted(receiptVerified: false, signed: true, EigenTrustEngine.DefaultTrustScore, 1, false));
        RatingWeight.Counted(receiptVerified: true, signed: true, 0.9, 1, false).Should().Be(0.9);
    }

    [Fact]
    public void ForgettingFollowsTime_NotTheNumberOfRatings()
    {
        // Before: every rating multiplied the history by 0.995, so 240 junk ratings a day erased
        // 70% of it. Now zero-weight ratings change nothing.
        var (service, now) = ProbedFor(100);
        // Let the time since the last probe pass first, so only the junk ratings differ.
        service = _scoring.ApplyRating(service, Observation(works: false, weight: 0), now);
        var before = _scoring.CalculateScore(service);

        for (var i = 0; i < 1000; i++)
            service = _scoring.ApplyRating(service, Observation(works: false, weight: 0), now);

        _scoring.CalculateScore(service).Score.Should().Be(before.Score);
    }

    [Fact]
    public void EvidenceHalvesEveryHalfLife()
    {
        BetaReputationSystem.DecayFactor(TimeSpan.FromDays(BetaReputationSystem.DefaultHalfLifeDays), BetaReputationSystem.DefaultHalfLifeDays)
            .Should().BeApproximately(0.5, 1e-12);
        BetaReputationSystem.DecayFactor(TimeSpan.Zero, 30).Should().Be(1.0);
        BetaReputationSystem.DecayFactor(TimeSpan.FromDays(-1), 30).Should().Be(1.0, "a clock step back must not inflate evidence");
    }

    [Fact]
    public void ADomainsScore_FollowsEvidence_NotTheNumberOfRatings()
    {
        // A thousand zero-weight ratings on one path must not drown the endpoint that holds the
        // evidence.
        var (healthy, now) = ProbedFor(30);
        healthy.Did = "victim.example/api";
        var flooded = new ServiceEntity { Did = "victim.example/junk" };
        for (var i = 0; i < 1000; i++)
            flooded = _scoring.ApplyRating(flooded, Observation(works: false, weight: 0.0001), now);

        var provider = _scoring.CalculateProviderScore("victim.example", new[] { healthy, flooded });

        provider.Score.Should().BeApproximately(_scoring.CalculateScore(healthy).Score, 0.01);
        provider.RatingsCount.Should().Be(healthy.RatingsCount + flooded.RatingsCount);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1234:5678:9abc::1", "2001:db8:1234:5600::/56")]
    [InlineData("2001:db8:1234:56ff:ffff::2", "2001:db8:1234:5600::/56")]
    public void AnIpv6Subscriber_IsOneCaller(string address, string bucket)
    {
        ClientAddress.Bucket(IPAddress.Parse(address)).Should().Be(bucket);
    }

    [Theory]
    [InlineData("did:web:trustscoreagent.com:probe", true)]
    [InlineData("did:web:TRUSTSCOREAGENT.COM.:probe", true)]
    [InlineData("did:web:api.trustscoreagent.com", true)]
    [InlineData("did:web:trustscoreagent.com%3A8443", true)]
    [InlineData("did:web:nottrustscoreagent.com", false)]
    [InlineData("did:web:trustscoreagent.com.evil.example", false)]
    [InlineData("did:key:z6MkhaXgBZDvotDkL5257faiztiGiC2QtKLGpbnnEGta2doK", false)]
    public void ProbeIdentities_AreReserved(string did, bool reserved)
    {
        TrustIdentity.IsReserved(did).Should().Be(reserved);
    }
}

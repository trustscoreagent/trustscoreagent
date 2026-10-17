using System.Collections.Concurrent;
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

public class X402ProberTests
{
    private const string ProbeDid = "did:web:trustscoreagent.com:probe";
    private const string CatalogUrl = "https://catalog.test/discovery/resources";

    private static X402CatalogEntry Entry(string resource, int payers, string? method = "GET", string? type = "http") =>
        new(resource, type, method, payers);

    // ---- catalog and selection ---------------------------------------------------------------

    [Fact]
    public void ParseCatalogPage_ReadsResourceMethodPayersAndTotal()
    {
        var json = """
                   {"items":[
                     {"resource":"https://a.test/x","type":"http","quality":{"l30DaysUniquePayers":42},
                      "extensions":{"bazaar":{"info":{"input":{"method":"POST","type":"http"}}}}},
                     {"resource":"https://b.test/y","type":"http"},
                     {"type":"http"},
                     "junk"],
                    "pagination":{"limit":2,"offset":0,"total":1234},"x402Version":2}
                   """;

        var (items, total) = X402Prober.ParseCatalogPage(json);

        total.Should().Be(1234);
        items.Should().Equal(
            new X402CatalogEntry("https://a.test/x", "http", "POST", 42),
            new X402CatalogEntry("https://b.test/y", "http", null, 0));
    }

    [Fact]
    public void Selection_KeepsHttpsEndpointsAboveThePayerThreshold_MostUsedFirst()
    {
        var selected = X402Prober.SelectTargets(
            [
                Entry("https://low.test/a", 10),          // not MORE than 10
                Entry("https://mid.test/a", 11),
                Entry("https://top.test/a?x=1", 500),
                Entry("http://plain.test/a", 900),        // not HTTPS
                Entry("https://port.test:8443/a", 900),   // not the default port
                Entry("https://user:pw@cred.test/a", 900),
                Entry("https://put.test/a", 900, "PUT"),
                Entry("https://mcp.test/a", 900, type: "mcp"),
            ],
            minUniquePayers: 10, maxPerHost: 20, maxTargets: 500);

        selected.Select(t => t.ServiceDid).Should().Equal("top.test/a", "mid.test/a");
        selected[0].Url.Should().Be("https://top.test/a?x=1", "the resource is probed exactly as listed");
        selected[0].Method.Should().Be("GET");
    }

    [Fact]
    public void Selection_CapsEachHost_AndTheTotal_AndProbesASameServiceOnce()
    {
        var crowded = Enumerable.Range(0, 30).Select(i => Entry($"https://crowded.test/e{i}", 1000 - i));
        var selected = X402Prober.SelectTargets(
            [
                .. crowded,
                Entry("https://other.test/a", 50),
                Entry("https://other.test/a?v=2", 40),    // same service once normalized
            ],
            minUniquePayers: 10, maxPerHost: 3, maxTargets: 500);

        selected.Count(t => t.ServiceDid.StartsWith("crowded.test/")).Should().Be(3);
        selected.Count(t => t.ServiceDid == "other.test/a").Should().Be(1);

        X402Prober.SelectTargets([.. crowded], 10, 20, maxTargets: 5).Should().HaveCount(5);
    }

    [Fact]
    public void Selection_LeavesOutExcludedHostsAndTheirSubdomains()
    {
        var selected = X402Prober.SelectTargets(
            [
                Entry("https://optout.test/a", 90),
                Entry("https://api.optout.test/b", 80),
                Entry("https://notoptout.test/c", 70),
            ],
            10, 20, 500, excludedHosts: [" OptOut.test. "]);

        selected.Select(t => t.ServiceDid).Should().Equal("notoptout.test/c");
    }

    [Fact]
    public void SpreadByHost_InterleavesProviders()
    {
        X402ProbeTarget T(string id) => new(id, "https://" + id, "GET", 1);
        var order = X402Prober.SpreadByHost([T("a.test/1"), T("a.test/2"), T("a.test/3"), T("b.test/1")])
            .Select(t => t.ServiceDid);

        order.Should().Equal("a.test/1", "b.test/1", "a.test/2", "a.test/3");
    }

    // ---- a probe pass --------------------------------------------------------------------------

    private sealed class CapturingRatingWriter : IRatingWriter
    {
        private readonly ConcurrentBag<Rating> _ratings = new();
        public IReadOnlyList<Rating> Ratings => _ratings.ToList();

        public Task SubmitAsync(string serviceId, RatingDelta delta, Rating rating)
        {
            _ratings.Add(rating);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryProbeHealth : IProbeHealthRepository
    {
        public ConcurrentDictionary<string, ProbeTargetHealth> Store { get; } = new();

        public Task<IReadOnlyDictionary<string, ProbeTargetHealth>> GetAllAsync() =>
            Task.FromResult<IReadOnlyDictionary<string, ProbeTargetHealth>>(new Dictionary<string, ProbeTargetHealth>(Store));

        public Task UpsertAsync(ProbeTargetHealth health)
        {
            Store[health.ServiceDid] = health;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryTargets : IX402TargetRepository
    {
        public List<X402ProbeTarget> Active { get; set; } = new();
        public DateTimeOffset? LastRefresh { get; set; }
        public int Replacements { get; private set; }

        public Task<IReadOnlyList<X402ProbeTarget>> GetActiveAsync() =>
            Task.FromResult<IReadOnlyList<X402ProbeTarget>>(Active.ToList());

        public Task<DateTimeOffset?> GetLastRefreshAsync() => Task.FromResult(LastRefresh);

        public Task ReplaceSelectionAsync(IReadOnlyList<X402ProbeTarget> targets, DateTimeOffset now)
        {
            Active = targets.ToList();
            LastRefresh = now;
            Replacements++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTrust : IAgentRepository
    {
        public Task<double> GetTrustScoreAsync(string agentDid) => Task.FromResult(0.8);
        public Task UpsertTrustScoresAsync(Dictionary<string, double> scores) => Task.CompletedTask;
    }

    private static HttpResponseMessage Challenge402()
    {
        var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("{}") };
        response.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", X402ChallengeTests.V2Header);
        return response;
    }

    private static HttpResponseMessage CatalogPage(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task<(CapturingRatingWriter Writer, InMemoryTargets Targets, InMemoryProbeHealth Health)> RunAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond, InMemoryTargets? targets = null, InMemoryProbeHealth? health = null)
    {
        var writer = new CapturingRatingWriter();
        targets ??= new InMemoryTargets();
        health ??= new InMemoryProbeHealth();
        var prober = new X402Prober(
            new StubHttpClientFactory(new StubHttpMessageHandler(respond)),
            new BetaReputationSystem(),
            writer,
            new FixedTrust(),
            health,
            targets,
            Options.Create(new X402ProbeOptions
            {
                Enabled = true,
                AgentDid = ProbeDid,
                CatalogUrl = CatalogUrl,
                CatalogPageSize = 2,
                CatalogPageDelay = TimeSpan.Zero,
                TimeoutSeconds = 3,
                MinUniquePayers = 10,
            }),
            NullLogger<X402Prober>.Instance);
        await prober.RunAsync();
        return (writer, targets, health);
    }

    [Fact]
    public async Task FirstPass_ReadsEveryCatalogPage_SelectsAndProbesWithoutPaying()
    {
        var requests = new ConcurrentBag<HttpRequestMessage>();
        var bodies = new ConcurrentDictionary<string, string>();
        var (writer, targets, _) = await RunAsync(req =>
        {
            requests.Add(req);
            var url = req.RequestUri!.ToString();
            if (url.StartsWith(CatalogUrl))
                return CatalogPage(url.Contains("offset=0")
                    ? """{"items":[{"resource":"https://paid.test/get","type":"http","quality":{"l30DaysUniquePayers":50}},{"resource":"https://paid.test/post","type":"http","quality":{"l30DaysUniquePayers":40},"extensions":{"bazaar":{"info":{"input":{"method":"POST"}}}}}],"pagination":{"total":3}}"""
                    : """{"items":[{"resource":"https://quiet.test/a","type":"http","quality":{"l30DaysUniquePayers":2}}],"pagination":{"total":3}}""");

            if (req.Content is not null) bodies[url] = req.Content.ReadAsStringAsync().Result;
            return Challenge402();
        });

        requests.Count(r => r.RequestUri!.ToString().StartsWith(CatalogUrl)).Should().Be(2, "both pages are read");
        targets.Active.Select(t => t.ServiceDid).Should().BeEquivalentTo("paid.test/get", "paid.test/post");

        requests.Should().OnlyContain(r => !r.Headers.Contains("X-PAYMENT") && !r.Headers.Contains("PAYMENT-SIGNATURE"),
            "the probe never pays");
        requests.Single(r => r.RequestUri!.AbsolutePath == "/post").Method.Should().Be(HttpMethod.Post);
        bodies["https://paid.test/post"].Should().Be("{}");

        writer.Ratings.Should().HaveCount(2).And.OnlyContain(r =>
            r.AgentDid == ProbeDid && r.Metrics.StatusCode == 402 && r.Metrics.SchemaValid == true
            && !r.HasReceipt && Math.Abs(r.Weight - 0.3 * 0.8) < 1e-9);
    }

    [Fact]
    public async Task ARecentSelection_IsNotRefreshed()
    {
        var targets = new InMemoryTargets
        {
            Active = [new X402ProbeTarget("paid.test/get", "https://paid.test/get", "GET", 50)],
            LastRefresh = DateTimeOffset.UtcNow.AddHours(-1),
        };
        var catalogCalls = 0;

        var (writer, _, _) = await RunAsync(req =>
        {
            if (req.RequestUri!.ToString().StartsWith(CatalogUrl)) Interlocked.Increment(ref catalogCalls);
            return Challenge402();
        }, targets);

        catalogCalls.Should().Be(0);
        targets.Replacements.Should().Be(0);
        writer.Ratings.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnreachableCatalog_KeepsTheCurrentSelection()
    {
        var targets = new InMemoryTargets
        {
            Active = [new X402ProbeTarget("paid.test/get", "https://paid.test/get", "GET", 50)],
            LastRefresh = DateTimeOffset.UtcNow.AddDays(-2),
        };

        var (writer, _, _) = await RunAsync(req => req.RequestUri!.ToString().StartsWith(CatalogUrl)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : Challenge402(), targets);

        targets.Replacements.Should().Be(0);
        writer.Ratings.Should().ContainSingle().Which.ServiceDid.Should().Be("paid.test/get");
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "garbage", false)]   // a broken challenge: non-conformant
    [InlineData(HttpStatusCode.OK, null, null)]                      // free after all: conformity unknown
    [InlineData(HttpStatusCode.BadRequest, null, null)]
    public async Task OtherAnswers_AreRecordedAsMeasured(HttpStatusCode status, string? header, bool? schemaValid)
    {
        var targets = new InMemoryTargets
        {
            Active = [new X402ProbeTarget("paid.test/get", "https://paid.test/get", "GET", 50)],
            LastRefresh = DateTimeOffset.UtcNow,
        };

        var (writer, _, _) = await RunAsync(_ =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            if (header is not null) r.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", header);
            return r;
        }, targets);

        var rating = writer.Ratings.Should().ContainSingle().Subject;
        rating.Metrics.StatusCode.Should().Be((int)status);
        rating.Metrics.SchemaValid.Should().Be(schemaValid);
    }

    [Fact]
    public async Task AnUnreachableEndpoint_IsA503AtTheFullTimeout()
    {
        var targets = new InMemoryTargets
        {
            Active = [new X402ProbeTarget("paid.test/get", "https://paid.test/get", "GET", 50)],
            LastRefresh = DateTimeOffset.UtcNow,
        };

        var (writer, _, _) = await RunAsync(_ => throw new HttpRequestException("connection refused"), targets);

        var rating = writer.Ratings.Should().ContainSingle().Subject;
        rating.Metrics.StatusCode.Should().Be(503);
        rating.Metrics.LatencyMs.Should().Be(3000);
        rating.Metrics.SchemaValid.Should().BeNull();
    }

    [Fact]
    public async Task ABrokenChallenge_KeepsBeingPublished_NeverQuarantined()
    {
        var targets = new InMemoryTargets
        {
            Active = [new X402ProbeTarget("paid.test/get", "https://paid.test/get", "GET", 50)],
            LastRefresh = DateTimeOffset.UtcNow,
        };
        var health = new InMemoryProbeHealth();
        health.Store["paid.test/get"] = ProbeTargetHealth.Unseen("paid.test/get") with
        {
            ConsecutiveFailures = 10,
            FailingSince = DateTimeOffset.UtcNow.AddDays(-10),
        };

        var (writer, _, _) = await RunAsync(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("not a challenge"),
        }, targets, health);

        writer.Ratings.Should().ContainSingle().Which.Metrics.SchemaValid.Should().BeFalse();
        health.Store["paid.test/get"].IsQuarantined.Should().BeFalse();
        health.Store["paid.test/get"].ConsecutiveFailures.Should().Be(0, "a 402 shows the endpoint is there");
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var calls = 0;
        var prober = new X402Prober(
            new StubHttpClientFactory(new StubHttpMessageHandler(_ => { calls++; return Challenge402(); })),
            new BetaReputationSystem(), new CapturingRatingWriter(), new FixedTrust(), new InMemoryProbeHealth(),
            new InMemoryTargets(), Options.Create(new X402ProbeOptions { Enabled = false }),
            NullLogger<X402Prober>.Instance);

        await prober.RunAsync();

        calls.Should().Be(0);
    }
}

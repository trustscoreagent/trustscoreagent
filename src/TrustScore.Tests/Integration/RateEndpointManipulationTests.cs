using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using TrustScore.Api.Endpoints;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// The weight rules of POST /v1/rate that bound what one source, or a crowd of new identities,
/// can do to a score: reserved probe identity, per-domain quotas, repeat ratings counting 1/n, the
/// newcomers' daily allowance, receipts bound to the call they cover, and no CORS on writes.
/// </summary>
public class RateEndpointManipulationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RateEndpointManipulationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed record Host(HttpClient Client, CapturingRatingWriter Writer, CapturingRateLimiter Limiter);

    private Host Create(double trust = EigenTrustEngine.DefaultTrustScore)
    {
        var writer = new CapturingRatingWriter();
        var limiter = new CapturingRateLimiter();
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["SkipMigrations"] = "true" }));
            builder.ConfigureServices(services =>
            {
                Replace<IServiceRepository>(services, new FakeServiceRepository());
                Replace<IRatingRepository>(services, new FakeRatingRepository());
                Replace<ICacheService>(services, new FakeCacheService());
                Replace<IReceiptVerifier>(services, new FakeReceiptVerifier());
                Replace<IDidResolver>(services, new FakeDidResolver());
                Replace<IAuditService>(services, new FakeAuditService());
                Replace<IAgentRepository>(services, new FixedTrustAgentRepository(trust));
                Replace<IRatingWriter>(services, writer);
                Replace<IRateLimiter>(services, limiter);
                Replace<StackExchange.Redis.IConnectionMultiplexer>(services,
                    StackExchange.Redis.ConnectionMultiplexer.Connect("localhost:1,abortConnect=false"));
            });
        }).CreateClient();
        return new Host(client, writer, limiter);
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
        services.AddSingleton(instance);
    }

    private static string Body(string service, int status = 200, string? receipt = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["service"] = service,
            ["metrics"] = new { status_code = status, latency_ms = 100 },
            ["receipt"] = receipt,
        });

    private static HttpRequestMessage Unsigned(string body, string agentDid = "did:key:zSomeone")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(AgentSignatureHeaders.DidHeader, agentDid);
        return request;
    }

    private static HttpRequestMessage Signed(string body, Key key)
    {
        var request = Unsigned(body);
        request.Headers.Remove(AgentSignatureHeaders.DidHeader);
        var h = AgentSigner.Sign(key, Encoding.UTF8.GetBytes(body));
        request.Headers.Add(AgentSignatureHeaders.DidHeader, h.AgentDid);
        request.Headers.Add(AgentSignatureHeaders.SignatureHeader, h.Signature);
        request.Headers.Add(AgentSignatureHeaders.TimestampHeader, h.Timestamp);
        request.Headers.Add(AgentSignatureHeaders.NonceHeader, h.Nonce);
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData("did:web:trustscoreagent.com:probe")]
    [InlineData("DID:WEB:TrustScoreAgent.com:probe")]
    [InlineData("did:web:trustscoreagent.com")]
    [InlineData("did:web:api.trustscoreagent.com")]
    [InlineData("did:web:trustscoreagent.com%3A8443:probe")]
    public async Task TheProbesIdentity_CannotBeUsedFromOutside(string did)
    {
        var host = Create();

        var response = await host.Client.SendAsync(Unsigned(Body("api.example.com"), did));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("error").GetString().Should().Be("reserved_agent_did");
        host.Writer.Last.Should().BeNull();
    }

    [Fact]
    public async Task EndpointsOfOneDomain_ShareOneQuota()
    {
        // Audit C1: each path used to be a fresh bucket, so evil.com/a1, /a2, ... were unlimited.
        var host = Create();

        await host.Client.SendAsync(Unsigned(Body("api.example.com/a1")));
        await host.Client.SendAsync(Unsigned(Body("api.example.com/a2")));

        host.Limiter.RatingKeys.Should().HaveCount(2);
        host.Limiter.RatingKeys.Distinct().Should().ContainSingle().Which.Should().EndWith(":api.example.com");
    }

    [Fact]
    public async Task RepeatRatingsFromOneSource_CountOneOverN()
    {
        var host = Create();

        await host.Client.SendAsync(Unsigned(Body("api.example.com/a1")));
        var first = host.Writer.Last!.Weight;
        var second = await host.Client.SendAsync(Unsigned(Body("api.example.com/a2"), "did:key:zRotated"));

        host.Writer.Last!.Weight.Should().BeApproximately(first / 2, 1e-9,
            "a new DID from the same address is the same source");
        var json = await Json(second);
        json.GetProperty("effective_weight").GetDouble().Should().BeApproximately(first / 2, 1e-6);
        json.GetProperty("weight_notes").EnumerateArray().Should().ContainSingle()
            .Which.GetString().Should().Contain("1/2");
    }

    [Fact]
    public async Task NewcomersPastTheDailyAllowance_AreRecordedAtZeroWeight()
    {
        // Many fresh keys from many places: the per-source limits do not stop them, the allowance
        // does. 31 keys from one test address stand in for 31 different addresses.
        var host = Create(trust: EigenTrustEngine.DefaultTrustScore);
        HttpResponseMessage last = null!;
        for (var i = 0; i <= RateEndpoints.NewcomerRatingsPerProviderPerDay; i++)
        {
            using var key = Key.Create(SignatureAlgorithm.Ed25519);
            last = await host.Client.SendAsync(Signed(Body("api.example.com"), key));
            last.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        host.Writer.Last!.Weight.Should().Be(0);
        (await Json(last)).GetProperty("weight_notes").EnumerateArray()
            .Should().Contain(n => n.GetString()!.Contains("zero weight"));
    }

    [Fact]
    public async Task AgentsWithEarnedTrust_AreNotBoundByTheNewcomersAllowance()
    {
        var host = Create(trust: 0.9);
        for (var i = 0; i <= RateEndpoints.NewcomerRatingsPerProviderPerDay; i++)
        {
            using var key = Key.Create(SignatureAlgorithm.Ed25519);
            await host.Client.SendAsync(Signed(Body("api.example.com"), key));
        }

        host.Writer.Last!.Weight.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AReceiptForTheRatedEndpoint_IsVerified()
    {
        // FakeReceiptVerifier vouches for a 200 on /test.
        var host = Create();

        var response = await host.Client.SendAsync(Unsigned(Body("api.example.com/test", 200, "valid-receipt")));

        (await Json(response)).GetProperty("rating_weight").GetString().Should().Be("verified");
        host.Writer.Last!.ReceiptVerified.Should().BeTrue();
    }

    [Theory]
    [InlineData("api.example.com/other", 200)]
    [InlineData("api.example.com/test", 503)]
    public async Task AReceiptForAnotherCall_CountsAsUnverified(string service, int status)
    {
        // Audit M1: one genuine receipt for a 200 on /cheap must not lend full weight to a 503
        // reported on /other.
        var host = Create();

        var response = await host.Client.SendAsync(Unsigned(Body(service, status, "valid-receipt")));

        var json = await Json(response);
        json.GetProperty("rating_weight").GetString().Should().Be("unverified");
        json.GetProperty("weight_notes").EnumerateArray().Should().Contain(n => n.GetString()!.Contains("different endpoint"));
        host.Writer.Last!.ReceiptVerified.Should().BeFalse();
    }

    [Fact]
    public async Task AReceipt_CoversAnyEndpointWhenTheDomainIsRated()
    {
        var host = Create();

        await host.Client.SendAsync(Unsigned(Body("api.example.com", 200, "valid-receipt")));

        host.Writer.Last!.ReceiptVerified.Should().BeTrue();
    }

    [Fact]
    public async Task RatingFromAWebPage_IsNotAllowedByCors()
    {
        var host = Create();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/rate");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-agent-did");

        var response = await host.Client.SendAsync(preflight);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task ReadingScoresFromAWebPage_IsStillAllowed()
    {
        var host = Create();
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/score?service=api.example.com");
        request.Headers.Add("Origin", "https://any.example");

        var response = await host.Client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("*");
    }

    /// <summary>Every agent has the same trust, so the tests can choose newcomer or trusted.</summary>
    private sealed class FixedTrustAgentRepository(double trust) : IAgentRepository
    {
        public Task<double> GetTrustScoreAsync(string agentDid) => Task.FromResult(trust);
        public Task UpsertTrustScoresAsync(Dictionary<string, double> scores) => Task.CompletedTask;
    }
}

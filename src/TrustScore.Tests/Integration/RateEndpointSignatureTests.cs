using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// Exercises X-Agent-Signature through the real /v1/rate pipeline, including the request-body
/// buffering that lets the handler re-read the exact bytes the signature covers. The signature
/// verifier is NOT faked here: these run against real Ed25519 verification.
/// </summary>
public class RateEndpointSignatureTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Body = """{"service":"api.example.com","metrics":{"status_code":200,"latency_ms":100}}""";
    private static readonly byte[] BodyBytes = Encoding.UTF8.GetBytes(Body);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Key _agentKey = Key.Create(SignatureAlgorithm.Ed25519);

    public RateEndpointSignatureTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed record TestContext(
        HttpClient Client,
        CapturingRatingWriter Writer,
        CapturingRateLimiter RateLimiter);

    private TestContext CreateClient()
    {
        var writer = new CapturingRatingWriter();
        var limiter = new CapturingRateLimiter();
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["SkipMigrations"] = "true" }));
            builder.ConfigureServices(services =>
            {
                ReplaceService<IServiceRepository, FakeServiceRepository>(services);
                ReplaceService<IRatingRepository, FakeRatingRepository>(services);
                ReplaceService<ICacheService, FakeCacheService>(services);
                ReplaceService<IReceiptVerifier, FakeReceiptVerifier>(services);
                ReplaceService<IDidResolver, FakeDidResolver>(services);
                ReplaceService<IAuditService, FakeAuditService>(services);
                ReplaceService<IAgentRepository, FakeAgentRepository>(services);

                // Capture what actually gets persisted, so weight policy can be asserted directly
                // rather than inferred from the resulting score.
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IRatingWriter));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IRatingWriter>(writer);

                // Capture the rate-limit keys so the tests can assert WHICH bucket a request spends,
                // which is the whole point of keying on a proven identity.
                var limiterDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IRateLimiter));
                if (limiterDescriptor is not null) services.Remove(limiterDescriptor);
                services.AddSingleton<IRateLimiter>(limiter);

                var redis = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
                if (redis is not null) services.Remove(redis);
                services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                    StackExchange.Redis.ConnectionMultiplexer.Connect("localhost:1"));
            });
        }).CreateClient();

        return new TestContext(client, writer, limiter);
    }

    private static void ReplaceService<TService, TImpl>(IServiceCollection services)
        where TService : class where TImpl : class, TService
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is not null) services.Remove(descriptor);
        services.AddSingleton<TService, TImpl>();
    }

    private static HttpRequestMessage Request(AgentSignatureHeaders? headers, string? agentDid = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            // Sent as raw bytes, never re-serialised, so the body hashed here is the body signed.
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(AgentSignatureHeaders.DidHeader, agentDid ?? headers?.AgentDid ?? "did:key:zUnsigned");
        if (headers?.Signature is not null) request.Headers.Add(AgentSignatureHeaders.SignatureHeader, headers.Signature);
        if (headers?.Timestamp is not null) request.Headers.Add(AgentSignatureHeaders.TimestampHeader, headers.Timestamp);
        if (headers?.Nonce is not null) request.Headers.Add(AgentSignatureHeaders.NonceHeader, headers.Nonce);

        return request;
    }

    [Fact]
    public async Task SignedRating_IsAccepted_AndMarkedSigned()
    {
        var (client, writer, _) = CreateClient();
        var headers = AgentSigner.Sign(_agentKey, BodyBytes);

        var response = await client.SendAsync(Request(headers));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("agent_identity").GetString().Should().Be("signed");
        writer.Last!.SignatureVerified.Should().BeTrue();
    }

    [Fact]
    public async Task UnsignedRating_StillWorks_ForExistingClients()
    {
        // The whole point of not requiring signatures outright: every client written before this
        // feature must keep working, just at reduced weight.
        var (client, writer, _) = CreateClient();

        var response = await client.SendAsync(Request(headers: null, agentDid: "did:web:legacy.example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("agent_identity").GetString().Should().Be("unsigned");
        writer.Last!.SignatureVerified.Should().BeFalse();
    }

    [Fact]
    public async Task UnsignedRating_WeighsHalfOfASignedOne()
    {
        var (signedClient, signedWriter, _) = CreateClient();
        await signedClient.SendAsync(Request(AgentSigner.Sign(_agentKey, BodyBytes)));

        var (unsignedClient, unsignedWriter, _) = CreateClient();
        await unsignedClient.SendAsync(Request(headers: null, agentDid: "did:web:legacy.example.com"));

        unsignedWriter.Last!.Weight.Should().BeApproximately(signedWriter.Last!.Weight * 0.5, 1e-9);
    }

    [Fact]
    public async Task ForgedSignature_Is401_NotSilentlyDowngraded()
    {
        // Signing with a different key while claiming the victim's DID must fail loudly. If it
        // fell through to the unsigned path, impersonation would still be free.
        var (client, writer, _) = CreateClient();
        using var attackerKey = Key.Create(SignatureAlgorithm.Ed25519);
        var forged = AgentSigner.Sign(attackerKey, BodyBytes, agentDid: AgentSigner.DidKeyFor(_agentKey));

        var response = await client.SendAsync(Request(forged));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        writer.Last.Should().BeNull("a rejected rating must not be persisted");
    }

    [Fact]
    public async Task SignatureOverADifferentBody_Is401()
    {
        // Proves the body really is bound by hash: the buffering has to hand the handler the exact
        // bytes that arrived, otherwise this would wrongly pass.
        var (client, _, _) = CreateClient();
        var otherBody = Encoding.UTF8.GetBytes("""{"service":"evil.example.com","metrics":{"status_code":200,"latency_ms":1}}""");
        var headers = AgentSigner.Sign(_agentKey, otherBody);

        var response = await client.SendAsync(Request(headers));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PartiallySignedRequest_Is401()
    {
        var (client, _, _) = CreateClient();
        var headers = AgentSigner.Sign(_agentKey, BodyBytes) with { Nonce = null };

        var response = await client.SendAsync(Request(headers));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignedRating_IsAccepted_WhenTheUrlCasingDiffers()
    {
        // Routing is case-insensitive, so this reaches the same endpoint. The client signs the
        // canonical route, so the signature must not depend on how the caller happened to spell
        // the URL, or a proxy rewriting case would produce inexplicable 401s.
        var (client, _, _) = CreateClient();
        var headers = AgentSigner.Sign(_agentKey, BodyBytes, path: "/v1/rate");

        var request = new HttpRequestMessage(HttpMethod.Post, "/V1/Rate")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(AgentSignatureHeaders.DidHeader, headers.AgentDid);
        request.Headers.Add(AgentSignatureHeaders.SignatureHeader, headers.Signature!);
        request.Headers.Add(AgentSignatureHeaders.TimestampHeader, headers.Timestamp!);
        request.Headers.Add(AgentSignatureHeaders.NonceHeader, headers.Nonce!);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- rate-limit bucketing: an asserted DID must not spend someone else's quota ---

    [Fact]
    public async Task UnsignedRating_DoesNotSpendTheClaimedAgentsQuota()
    {
        // The attack this prevents: send junk unsigned ratings naming a victim's DID until their
        // bucket is empty, locking the real agent out for an hour.
        var (client, _, limiter) = CreateClient();
        var victimDid = AgentSigner.DidKeyFor(_agentKey);

        await client.SendAsync(Request(headers: null, agentDid: victimDid));

        limiter.RatingKeys.Should().ContainSingle();
        limiter.RatingKeys[0].Should().NotContain(victimDid, "an asserted DID must not key the bucket");
        limiter.RatingKeys[0].Should().StartWith("ip:");
    }

    [Fact]
    public async Task SignedRating_SpendsItsOwnProvenBucket()
    {
        var (client, _, limiter) = CreateClient();
        var did = AgentSigner.DidKeyFor(_agentKey);

        await client.SendAsync(Request(AgentSigner.Sign(_agentKey, BodyBytes)));

        limiter.RatingKeys.Should().Contain($"agent:{did}:api.example.com");
    }

    [Fact]
    public async Task RotatingSigningKeys_DoesNotMintFreshQuota()
    {
        // A did:key costs nothing to create. If only the per-agent bucket applied, signing each
        // request with a new key would hand out a new allowance every time, which is the same
        // hole the unsigned path had before it was bucketed by caller.
        var (client, _, limiter) = CreateClient();

        using var first = Key.Create(SignatureAlgorithm.Ed25519);
        using var second = Key.Create(SignatureAlgorithm.Ed25519);
        await client.SendAsync(Request(AgentSigner.Sign(first, BodyBytes)));
        await client.SendAsync(Request(AgentSigner.Sign(second, BodyBytes)));

        var callerBuckets = limiter.RatingKeys.Where(k => k.StartsWith("ip-signed:")).ToList();
        callerBuckets.Should().HaveCount(2);
        callerBuckets.Distinct().Should().ContainSingle("both keys spend the same caller bucket");
    }

    [Fact]
    public async Task SignedRating_IsRejected_OnceTheCallerBucketIsSpent()
    {
        var (client, _, limiter) = CreateClient();
        limiter.ExhaustPrefix = "ip-signed:";

        var response = await client.SendAsync(Request(AgentSigner.Sign(_agentKey, BodyBytes)));

        response.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task RotatingAnAssertedDid_DoesNotMintFreshQuota()
    {
        // Before the buckets were keyed on a proven identity, rotating X-Agent-DID handed the
        // caller a brand new allowance each time, which made the per-agent limit meaningless.
        var (client, _, limiter) = CreateClient();

        await client.SendAsync(Request(headers: null, agentDid: "did:web:one.example.com"));
        await client.SendAsync(Request(headers: null, agentDid: "did:web:two.example.com"));

        limiter.RatingKeys.Should().HaveCount(2);
        limiter.RatingKeys.Distinct().Should().ContainSingle("both requests share the caller's bucket");
    }

    [Fact]
    public async Task ForgedSignature_DoesNotSpendTheVictimsQuota()
    {
        // A 401 must be refused before any bucket is touched, otherwise forging is still a way to
        // drain the victim's allowance.
        var (client, _, limiter) = CreateClient();
        using var attackerKey = Key.Create(SignatureAlgorithm.Ed25519);
        var forged = AgentSigner.Sign(attackerKey, BodyBytes, agentDid: AgentSigner.DidKeyFor(_agentKey));

        var response = await client.SendAsync(Request(forged));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        limiter.RatingKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayedSignature_Is401_OnSecondSubmission()
    {
        // Same client, so the nonce store is shared between the two calls.
        var (client, _, _) = CreateClient();
        var headers = AgentSigner.Sign(_agentKey, BodyBytes, nonce: "replay-nonce-value");

        (await client.SendAsync(Request(headers))).StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await client.SendAsync(Request(headers));

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

internal sealed class CapturingRateLimiter : IRateLimiter
{
    private readonly Dictionary<string, int> _counts = new();

    /// <summary>Keys checked by /v1/rate, excluding the per-IP global middleware bucket.</summary>
    public List<string> RatingKeys { get; } = new();

    /// <summary>Buckets with this prefix report as already exhausted.</summary>
    public string? ExhaustPrefix { get; set; }

    public Task<RateLimitResult> CheckAsync(string key, int maxRequests, TimeSpan window)
    {
        if (!key.StartsWith("global:", StringComparison.Ordinal))
            RatingKeys.Add(key);

        if (ExhaustPrefix is not null && key.StartsWith(ExhaustPrefix, StringComparison.Ordinal))
            return Task.FromResult(new RateLimitResult(false, maxRequests + 1, maxRequests));

        _counts.TryGetValue(key, out var count);
        count++;
        _counts[key] = count;
        return Task.FromResult(new RateLimitResult(count <= maxRequests, count, maxRequests));
    }
}

internal sealed class CapturingRatingWriter : IRatingWriter
{
    public Rating? Last { get; private set; }

    public Task SubmitAsync(string serviceId, RatingDelta delta, Rating rating)
    {
        Last = rating;
        return Task.CompletedTask;
    }
}

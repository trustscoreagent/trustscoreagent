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

    private (HttpClient Client, CapturingRatingWriter Writer) CreateClient()
    {
        var writer = new CapturingRatingWriter();
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["SkipMigrations"] = "true" }));
            builder.ConfigureServices(services =>
            {
                ReplaceService<IServiceRepository, FakeServiceRepository>(services);
                ReplaceService<IRatingRepository, FakeRatingRepository>(services);
                ReplaceService<ICacheService, FakeCacheService>(services);
                ReplaceService<IRateLimiter, FakeRateLimiter>(services);
                ReplaceService<IReceiptVerifier, FakeReceiptVerifier>(services);
                ReplaceService<IDidResolver, FakeDidResolver>(services);
                ReplaceService<IAuditService, FakeAuditService>(services);
                ReplaceService<IAgentRepository, FakeAgentRepository>(services);

                // Capture what actually gets persisted, so weight policy can be asserted directly
                // rather than inferred from the resulting score.
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IRatingWriter));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IRatingWriter>(writer);

                var redis = services.SingleOrDefault(d =>
                    d.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
                if (redis is not null) services.Remove(redis);
                services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                    StackExchange.Redis.ConnectionMultiplexer.Connect("localhost:1"));
            });
        }).CreateClient();

        return (client, writer);
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
        var (client, writer) = CreateClient();
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
        var (client, writer) = CreateClient();

        var response = await client.SendAsync(Request(headers: null, agentDid: "did:web:legacy.example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("agent_identity").GetString().Should().Be("unsigned");
        writer.Last!.SignatureVerified.Should().BeFalse();
    }

    [Fact]
    public async Task UnsignedRating_WeighsHalfOfASignedOne()
    {
        var (signedClient, signedWriter) = CreateClient();
        await signedClient.SendAsync(Request(AgentSigner.Sign(_agentKey, BodyBytes)));

        var (unsignedClient, unsignedWriter) = CreateClient();
        await unsignedClient.SendAsync(Request(headers: null, agentDid: "did:web:legacy.example.com"));

        unsignedWriter.Last!.Weight.Should().BeApproximately(signedWriter.Last!.Weight * 0.5, 1e-9);
    }

    [Fact]
    public async Task ForgedSignature_Is401_NotSilentlyDowngraded()
    {
        // Signing with a different key while claiming the victim's DID must fail loudly. If it
        // fell through to the unsigned path, impersonation would still be free.
        var (client, writer) = CreateClient();
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
        var (client, _) = CreateClient();
        var otherBody = Encoding.UTF8.GetBytes("""{"service":"evil.example.com","metrics":{"status_code":200,"latency_ms":1}}""");
        var headers = AgentSigner.Sign(_agentKey, otherBody);

        var response = await client.SendAsync(Request(headers));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PartiallySignedRequest_Is401()
    {
        var (client, _) = CreateClient();
        var headers = AgentSigner.Sign(_agentKey, BodyBytes) with { Nonce = null };

        var response = await client.SendAsync(Request(headers));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReplayedSignature_Is401_OnSecondSubmission()
    {
        // Same client, so the nonce store is shared between the two calls.
        var (client, _) = CreateClient();
        var headers = AgentSigner.Sign(_agentKey, BodyBytes, nonce: "replay-nonce-value");

        (await client.SendAsync(Request(headers))).StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await client.SendAsync(Request(headers));

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

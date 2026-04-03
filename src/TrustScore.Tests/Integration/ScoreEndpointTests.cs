using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Integration;

public class ScoreEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ScoreEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = CreateTestClient(factory);
    }

    [Fact]
    public async Task GetScore_UnknownService_Returns404()
    {
        var response = await _client.GetAsync("/v1/score?did=did:web:unknown.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Be("service_not_found");
    }

    [Fact]
    public async Task GetScore_MissingDid_Returns400()
    {
        var response = await _client.GetAsync("/v1/score?did=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Be("missing_did");
    }

    [Fact]
    public async Task GetScore_KnownService_ReturnsScore()
    {
        var response = await _client.GetAsync("/v1/score?did=did:web:seeded.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"score\"");
        body.Should().Contain("\"dimensions\"");
        body.Should().Contain("seeded.example.com");
    }

    internal static HttpClient CreateTestClient(WebApplicationFactory<Program> factory)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace real implementations with fakes
                ReplaceService<IServiceRepository, FakeServiceRepository>(services);
                ReplaceService<IRatingRepository, FakeRatingRepository>(services);
                ReplaceService<ICacheService, FakeCacheService>(services);

                // Remove Redis (not needed with FakeCacheService)
                var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
                if (redisDescriptor != null) services.Remove(redisDescriptor);
                // Add a dummy so DI doesn't fail if anything still resolves it
                services.AddSingleton<IConnectionMultiplexer>(sp =>
                    ConnectionMultiplexer.Connect("localhost:1")); // Won't actually connect
            });
        }).CreateClient();
    }

    private static void ReplaceService<TInterface, TImpl>(IServiceCollection services)
        where TInterface : class where TImpl : class, TInterface
    {
        var existing = services.Where(d => d.ServiceType == typeof(TInterface)).ToList();
        foreach (var d in existing) services.Remove(d);
        services.AddSingleton<TInterface, TImpl>();
    }

    private record ErrorResponse(string Error, string Message);
}

public class RateEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public RateEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = ScoreEndpointTests.CreateTestClient(factory);
    }

    [Fact]
    public async Task Rate_ValidRequest_Returns200()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:new-service.example.com",
                metrics = new { status_code = 200, latency_ms = 150, schema_valid = true }
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"accepted\":true");
    }

    [Fact]
    public async Task Rate_MissingAgentDid_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:some-service.example.com",
                metrics = new { status_code = 200, latency_ms = 100 }
            })
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("missing_agent_did");
    }

    [Fact]
    public async Task Rate_MissingServiceDid_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                metrics = new { status_code = 200, latency_ms = 100 }
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("missing_service_did");
    }

    [Fact]
    public async Task Rate_InvalidLatency_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:some.example.com",
                metrics = new { status_code = 200, latency_ms = 0 }
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_latency");
    }

    [Fact]
    public async Task Rate_InvalidQualityScore_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:some.example.com",
                metrics = new { status_code = 200, latency_ms = 100 },
                quality_score = 10
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_quality_score");
    }

    [Fact]
    public async Task Rate_WithReceipt_ReturnsVerifiedWeight()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:receipted.example.com",
                metrics = new { status_code = 200, latency_ms = 100, schema_valid = true },
                receipt = "eyJhbGciOiJFZERTQSJ9.eyJ0ZXN0IjoidmFsdWUifQ.fake-signature"
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"rating_weight\":\"verified\"");
    }
}

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = ScoreEndpointTests.CreateTestClient(factory);
    }

    [Fact]
    public async Task Health_ReturnsVersionField()
    {
        var response = await _client.GetAsync("/health");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"version\":\"0.1.0\"");
    }

    [Fact]
    public async Task Health_ReturnsJsonContentType()
    {
        var response = await _client.GetAsync("/health");

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }
}

#region Fakes

internal class FakeServiceRepository : IServiceRepository
{
    private readonly Dictionary<string, ServiceEntity> _services = new()
    {
        ["did:web:seeded.example.com"] = new ServiceEntity
        {
            Did = "did:web:seeded.example.com",
            Alpha = 10,
            Beta = 2,
            AlphaAvailability = 10,
            BetaAvailability = 2,
            AlphaLatency = 8,
            BetaLatency = 3,
            AlphaConformity = 9,
            BetaConformity = 2,
            RatingsCount = 50,
            SupportsReceipts = true,
            LastRatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        }
    };

    public Task<ServiceEntity?> GetByDidAsync(string did)
        => Task.FromResult(_services.GetValueOrDefault(did));

    public Task UpsertAsync(ServiceEntity service)
    {
        _services[service.Did] = service;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string did)
        => Task.FromResult(_services.ContainsKey(did));
}

internal class FakeRatingRepository : IRatingRepository
{
    private readonly List<Rating> _ratings = new();

    public Task InsertAsync(Rating rating)
    {
        _ratings.Add(rating);
        return Task.CompletedTask;
    }

    public Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window)
        => Task.FromResult(_ratings.Count(r =>
            r.AgentDid == agentDid &&
            r.ServiceDid == serviceDid &&
            r.CreatedAt > DateTimeOffset.UtcNow - window));
}

internal class FakeCacheService : ICacheService
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key)
        => Task.FromResult(_store.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, TimeSpan expiry)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync()
        => Task.FromResult(true);
}

#endregion

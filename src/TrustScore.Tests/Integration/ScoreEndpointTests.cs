using System.Data;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
    public async Task GetScore_UnknownService_ReturnsNeutralScore()
    {
        var response = await _client.GetAsync("/v1/score?did=did:web:unknown.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"score\":0.5");
        body.Should().Contain("\"ratings_count\":0");
        body.Should().Contain("\"known\":false");
    }

    [Fact]
    public async Task GetScore_MissingDid_Returns400()
    {
        var response = await _client.GetAsync("/v1/score?did=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.Should().Be("missing_service");
    }

    [Fact]
    public async Task GetScore_ProviderLevel_ReturnsAggregatedScore()
    {
        var response = await _client.GetAsync("/v1/score?service=seeded.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"score\"");
        body.Should().Contain("\"dimensions\"");
        body.Should().Contain("seeded.example.com");
        body.Should().Contain("\"level\":\"provider\"");
        body.Should().Contain("\"known\":true");
    }

    [Fact]
    public async Task GetScore_EndpointLevel_ReturnsSpecificScore()
    {
        var response = await _client.GetAsync("/v1/score?service=https://seeded.example.com/v1/translate");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("seeded.example.com/v1/translate");
        body.Should().Contain("\"level\":\"endpoint\"");
        body.Should().Contain("\"known\":true");
    }

    [Fact]
    public async Task GetScore_DifferentEndpoints_HaveDifferentScores()
    {
        var resp1 = await _client.GetAsync("/v1/score?service=seeded.example.com/v1/translate");
        var resp2 = await _client.GetAsync("/v1/score?service=seeded.example.com/v1/summarize");

        var body1 = await resp1.Content.ReadAsStringAsync();
        var body2 = await resp2.Content.ReadAsStringAsync();

        // translate is much better rated than summarize
        body1.Should().NotBe(body2);
    }

    internal static HttpClient CreateTestClient(WebApplicationFactory<Program> factory)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SkipMigrations"] = "true",
                });
            });
            builder.ConfigureServices(services =>
            {
                // Replace real implementations with fakes
                ReplaceService<IServiceRepository, FakeServiceRepository>(services);
                ReplaceService<IRatingRepository, FakeRatingRepository>(services);
                ReplaceService<ICacheService, FakeCacheService>(services);
                ReplaceService<IRateLimiter, FakeRateLimiter>(services);
                ReplaceService<IReceiptVerifier, FakeReceiptVerifier>(services);
                ReplaceService<IDidResolver, FakeDidResolver>(services);
                ReplaceService<IAuditService, FakeAuditService>(services);
                ReplaceService<IAgentRepository, FakeAgentRepository>(services);
                ReplaceService<IRatingWriter, FakeRatingWriter>(services);

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
    public async Task Rate_WithValidReceipt_ReturnsVerifiedWeight()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:receipted.example.com",
                metrics = new { status_code = 200, latency_ms = 100, schema_valid = true },
                receipt = "valid-receipt-jwt-token"  // FakeReceiptVerifier treats "valid-" prefix as verified
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"rating_weight\":\"verified\"");
    }

    [Fact]
    public async Task Rate_WithInvalidReceipt_ReturnsUnverifiedWeight()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:bad-receipt.example.com",
                metrics = new { status_code = 200, latency_ms = 100 },
                receipt = "invalid-receipt-jwt"  // FakeReceiptVerifier treats this as invalid signature
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"rating_weight\":\"unverified\"");
    }

    [Fact]
    public async Task Rate_WithReplayedReceipt_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:replay.example.com",
                metrics = new { status_code = 200, latency_ms = 100 },
                receipt = "replay-nonce-already-used"  // FakeReceiptVerifier treats "replay-" prefix as nonce replay
            })
        };
        request.Headers.Add("X-Agent-DID", "did:web:test-agent.example.com");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("nonce_replay");
    }

    [Fact]
    public async Task Rate_ExceedsRateLimit_Returns429()
    {
        // Submit 11 ratings (max is 10 per hour per agent per service)
        for (int i = 0; i < 10; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
            {
                Content = JsonContent.Create(new
                {
                    service_did = "did:web:ratelimit-test.example.com",
                    metrics = new { status_code = 200, latency_ms = 100 }
                })
            };
            req.Headers.Add("X-Agent-DID", "did:web:ratelimit-agent.example.com");
            var res = await _client.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The 11th should be rate limited
        var lastReq = new HttpRequestMessage(HttpMethod.Post, "/v1/rate")
        {
            Content = JsonContent.Create(new
            {
                service_did = "did:web:ratelimit-test.example.com",
                metrics = new { status_code = 200, latency_ms = 100 }
            })
        };
        lastReq.Headers.Add("X-Agent-DID", "did:web:ratelimit-agent.example.com");

        var lastRes = await _client.SendAsync(lastReq);

        lastRes.StatusCode.Should().Be((HttpStatusCode)429);
        var body = await lastRes.Content.ReadAsStringAsync();
        body.Should().Contain("rate_limited");
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
        body.Should().Contain("\"version\":");
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
        // Provider-level entry (legacy/direct domain rating)
        ["seeded.example.com"] = new ServiceEntity
        {
            Did = "seeded.example.com",
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
        },
        // Endpoint-level entries under same provider
        ["seeded.example.com/v1/translate"] = new ServiceEntity
        {
            Did = "seeded.example.com/v1/translate",
            Alpha = 15,
            Beta = 1,
            AlphaAvailability = 15,
            BetaAvailability = 1,
            AlphaLatency = 12,
            BetaLatency = 2,
            AlphaConformity = 14,
            BetaConformity = 1,
            RatingsCount = 30,
            SupportsReceipts = true,
            LastRatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        },
        ["seeded.example.com/v1/summarize"] = new ServiceEntity
        {
            Did = "seeded.example.com/v1/summarize",
            Alpha = 5,
            Beta = 8,
            AlphaAvailability = 6,
            BetaAvailability = 7,
            AlphaLatency = 4,
            BetaLatency = 9,
            AlphaConformity = 5,
            BetaConformity = 8,
            RatingsCount = 20,
            SupportsReceipts = false,
            LastRatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        },
    };

    public Task<ServiceEntity?> GetByDidAsync(string did)
        => Task.FromResult(_services.GetValueOrDefault(did));

    public Task<IReadOnlyList<ServiceEntity>> GetByDidsAsync(IReadOnlyList<string> dids)
    {
        var result = dids.Select(d => _services.GetValueOrDefault(d)).Where(s => s is not null).ToList()!;
        return Task.FromResult<IReadOnlyList<ServiceEntity>>(result!);
    }

    public Task<IReadOnlyList<ServiceEntity>> GetByProviderAsync(string provider)
    {
        var result = _services.Values
            .Where(s => s.Did.StartsWith($"{provider}/"))
            .ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<ServiceEntity>>(result);
    }

    public Task UpsertAsync(ServiceEntity service)
    {
        _services[service.Did] = service;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServiceEntity>> ListAsync(ServiceListFilter filter)
    {
        var query = _services.Values.AsEnumerable();

        if (filter.MinRatings.HasValue)
            query = query.Where(s => s.RatingsCount >= filter.MinRatings.Value);

        if (filter.MinScore.HasValue)
            query = query.Where(s => s.Alpha / (s.Alpha + s.Beta) >= filter.MinScore.Value);

        query = filter.SortBy switch
        {
            "ratings_count" => filter.Order == "asc"
                ? query.OrderBy(s => s.RatingsCount)
                : query.OrderByDescending(s => s.RatingsCount),
            "last_rated" => filter.Order == "asc"
                ? query.OrderBy(s => s.LastRatedAt)
                : query.OrderByDescending(s => s.LastRatedAt),
            _ => filter.Order == "asc"
                ? query.OrderBy(s => s.Alpha / (s.Alpha + s.Beta))
                : query.OrderByDescending(s => s.Alpha / (s.Alpha + s.Beta)),
        };

        var results = query.Skip(filter.Offset).Take(filter.Limit).ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<ServiceEntity>>(results);
    }

    public Task ApplyRatingAtomicAsync(string did, RatingDelta delta)
    {
        if (!_services.TryGetValue(did, out var svc))
        {
            svc = new ServiceEntity { Did = did };
            _services[did] = svc;
        }
        svc.Alpha = Math.Max(1.0, svc.Alpha * 0.995 + delta.AlphaDelta);
        svc.Beta = Math.Max(1.0, svc.Beta * 0.995 + delta.BetaDelta);
        svc.RatingsCount++;
        svc.LastRatedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task ApplyRatingAtomicAsync(IDbConnection conn, IDbTransaction tx, string did, RatingDelta delta)
        => ApplyRatingAtomicAsync(did, delta);

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

    public Task InsertAsync(IDbConnection conn, IDbTransaction tx, Rating rating)
        => InsertAsync(rating);

    public Task<int> CountRecentAsync(string agentDid, string serviceDid, TimeSpan window)
        => Task.FromResult(_ratings.Count(r =>
            r.AgentDid == agentDid &&
            r.ServiceDid == serviceDid &&
            r.CreatedAt > DateTimeOffset.UtcNow - window));

    public Task<RatingLeafInfo?> GetLeafInfoAsync(Guid ratingId)
    {
        var rating = _ratings.FirstOrDefault(r => r.Id == ratingId);
        if (rating is null) return Task.FromResult<RatingLeafInfo?>(null);
        return Task.FromResult<RatingLeafInfo?>(
            new RatingLeafInfo(rating.Id, rating.ServiceDid, rating.CreatedAt, "fakehash"));
    }

    public Task<IReadOnlyList<RatingLeafInfo>> GetAllLeafHashesAsync()
    {
        var result = _ratings.Select(r =>
            new RatingLeafInfo(r.Id, r.ServiceDid, r.CreatedAt, "fakehash")).ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<RatingLeafInfo>>(result);
    }

    public Task<IReadOnlyList<RatingLeafInfo>> GetAnchoredLeafHashesAsync(int leafCount)
    {
        var result = _ratings
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Take(leafCount)
            .Select(r => new RatingLeafInfo(r.Id, r.ServiceDid, r.CreatedAt, "fakehash"))
            .ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<RatingLeafInfo>>(result);
    }

    public Task<IReadOnlyList<AgentRatingRecord>> GetAllRatingsForTrustAsync()
    {
        var result = _ratings.Select(r => new AgentRatingRecord(
            r.AgentDid, r.ServiceDid, r.Metrics.StatusCode, r.Metrics.LatencyMs,
            r.Metrics.SchemaValid, r.ReceiptVerified)).ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<AgentRatingRecord>>(result);
    }

    public Task<IReadOnlyList<RatingSummary>> GetHistoryAsync(string serviceDid, int months)
    {
        var result = _ratings
            .Where(r => r.ServiceDid == serviceDid)
            .Select(r => new RatingSummary(
                r.CreatedAt, r.Metrics.StatusCode, r.Metrics.LatencyMs,
                r.Metrics.SchemaValid, r.QualityScore,
                r.HasReceipt, r.ReceiptVerified, r.Weight))
            .ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<RatingSummary>>(result);
    }

    public Task<IReadOnlyList<DailyHistoryPoint>> GetDailyHistoryAsync(string serviceDid, int months)
    {
        var since = DateTimeOffset.UtcNow.AddMonths(-months);
        var result = _ratings
            .Where(r => r.ServiceDid == serviceDid && r.CreatedAt > since)
            .GroupBy(r => r.CreatedAt.UtcDateTime.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyHistoryPoint(
                g.Key,
                g.Count(),
                (int)g.Average(r => r.Metrics.LatencyMs),
                g.Count(r => r.Metrics.StatusCode is >= 200 and < 300) / (double)g.Count(),
                g.Where(r => r.QualityScore.HasValue).Select(r => (double)r.QualityScore!.Value).DefaultIfEmpty(0).Average(),
                g.Count(r => r.ReceiptVerified)))
            .ToList().AsReadOnly();
        return Task.FromResult<IReadOnlyList<DailyHistoryPoint>>(result);
    }
}

internal class FakeRatingWriter : IRatingWriter
{
    private readonly IServiceRepository _services;
    private readonly IRatingRepository _ratings;

    public FakeRatingWriter(IServiceRepository services, IRatingRepository ratings)
    {
        _services = services;
        _ratings = ratings;
    }

    public async Task SubmitAsync(string serviceId, RatingDelta delta, Rating rating)
    {
        await _services.ApplyRatingAtomicAsync(serviceId, delta);
        await _ratings.InsertAsync(rating);
    }
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

    public Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry)
    {
        if (_store.ContainsKey(key))
            return Task.FromResult(false);
        _store[key] = value;
        return Task.FromResult(true);
    }

    public Task RemoveAsync(string key)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync()
        => Task.FromResult(true);
}

internal class FakeRateLimiter : IRateLimiter
{
    private readonly Dictionary<string, int> _counters = new();

    public Task<RateLimitResult> CheckAsync(string key, int maxRequests, TimeSpan window)
    {
        _counters.TryGetValue(key, out var count);
        count++;
        _counters[key] = count;
        return Task.FromResult(new RateLimitResult(count <= maxRequests, count, maxRequests));
    }
}

internal class FakeReceiptVerifier : IReceiptVerifier
{
    public Task<ReceiptVerificationResult> VerifyAsync(string jwt, string expectedServiceDid)
    {
        // Simulate: JWT starting with "valid-" is treated as verified
        if (jwt.StartsWith("valid-"))
            return Task.FromResult(ReceiptVerificationResult.Verified(new ReceiptPayload
            {
                ServiceDid = expectedServiceDid,
                AgentDid = "did:web:test-agent.example.com",
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Nonce = Guid.NewGuid().ToString(),
                Endpoint = "/test",
                Method = "POST",
                StatusCode = 200,
            }));

        if (jwt.StartsWith("replay-"))
            return Task.FromResult(ReceiptVerificationResult.Rejected(
                ReceiptVerificationStatus.NonceAlreadyUsed));

        // Default: unverified (like a malformed JWT)
        return Task.FromResult(ReceiptVerificationResult.Failed(
            ReceiptVerificationStatus.InvalidSignature));
    }
}

internal class FakeDidResolver : IDidResolver
{
    public Task<byte[]?> ResolvePublicKeyAsync(string did)
        => Task.FromResult<byte[]?>(null);
}

internal class FakeAgentRepository : IAgentRepository
{
    private readonly Dictionary<string, double> _scores = new();

    public Task<double> GetTrustScoreAsync(string agentDid)
        => Task.FromResult(_scores.GetValueOrDefault(agentDid, 0.5));

    public Task UpsertTrustScoresAsync(Dictionary<string, double> scores)
    {
        foreach (var (k, v) in scores) _scores[k] = v;
        return Task.CompletedTask;
    }
}

internal class FakeAuditService : IAuditService
{
    public Task<MerkleAnchor?> GetLatestAnchorAsync()
        => Task.FromResult<MerkleAnchor?>(null);

    public Task<InclusionProofResult?> GetInclusionProofAsync(Guid ratingId)
        => Task.FromResult<InclusionProofResult?>(null);
}

#endregion

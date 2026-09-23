using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// An agent's trust lives under two identities since signatures exist. The endpoint used to read
/// only the bare DID, so an agent that had never signed saw a value frozen from before the split
/// (or the neutral default) while its real, live score sat under the namespaced key.
/// </summary>
public class AgentTrustEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Did = "did:web:agent.example.com";

    private readonly WebApplicationFactory<Program> _factory;

    public AgentTrustEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient ClientWith(Dictionary<string, double> scores)
    {
        var agents = new FakeAgentRepository();
        agents.UpsertTrustScoresAsync(scores).GetAwaiter().GetResult();

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["SkipMigrations"] = "true" }));
            builder.ConfigureServices(services =>
            {
                Replace<ICacheService>(services, new FakeCacheService());
                Replace<IRateLimiter>(services, new FakeRateLimiter());
                Replace<IAgentRepository>(services, agents);
            });
        }).CreateClient();
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        var existing = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in existing) services.Remove(d);
        services.AddSingleton(instance);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string did)
    {
        var response = await client.GetAsync($"/v1/agent/trust?did={Uri.EscapeDataString(did)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task AgentThatNeverSigned_SeesItsLiveScore()
    {
        var client = ClientWith(new() { [TrustIdentity.For(Did, signatureVerified: false)] = 0.34 });

        var body = await GetAsync(client, Did);

        body.GetProperty("unsigned_trust_score").GetDouble().Should().Be(0.34);
        body.GetProperty("trust_score").GetDouble().Should().Be(0.5, "it has signed nothing, so neutral");
    }

    [Fact]
    public async Task BothIdentities_AreReportedSeparately()
    {
        var client = ClientWith(new()
        {
            [TrustIdentity.For(Did, signatureVerified: true)] = 0.9,
            [TrustIdentity.For(Did, signatureVerified: false)] = 0.2,
        });

        var body = await GetAsync(client, Did);

        body.GetProperty("trust_score").GetDouble().Should().Be(0.9);
        body.GetProperty("interpretation").GetString().Should().Be("HIGH");
        body.GetProperty("unsigned_trust_score").GetDouble().Should().Be(0.2);
        body.GetProperty("unsigned_interpretation").GetString().Should().Be("LOW");
    }

    [Fact]
    public async Task NamespacedSpelling_IsAcceptedAsInput()
    {
        var client = ClientWith(new() { [TrustIdentity.For(Did, signatureVerified: false)] = 0.34 });

        var body = await GetAsync(client, TrustIdentity.For(Did, signatureVerified: false));

        body.GetProperty("agent").GetString().Should().Be(Did);
        body.GetProperty("unsigned_trust_score").GetDouble().Should().Be(0.34);
    }
}

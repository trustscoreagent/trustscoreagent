using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// Guards the agent-discovery files served from public/. These returned 500 in production once
/// (Results.File needs an absolute path) and the deploy smoke test, which only hits /health,
/// did not catch it.
/// </summary>
public class DiscoveryEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DiscoveryEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = ScoreEndpointTests.CreateTestClient(factory);
    }

    [Fact]
    public async Task AgentJson_IsServed_AndIsValidAgentCard()
    {
        var response = await _client.GetAsync("/.well-known/agent.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body); // throws if not valid JSON
        doc.RootElement.GetProperty("protocolVersion").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("skills").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LlmsTxt_IsServed()
    {
        var response = await _client.GetAsync("/llms.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("TrustScoreAgent");
    }

    [Fact]
    public async Task Root_IsServed_AndPointsToDocs()
    {
        // The API root was a 404, which Search Console flagged as "Introuvable (404)". It now
        // returns a small JSON index pointing at the human documentation.
        var response = await _client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("documentation").GetString().Should().Be("https://trustscoreagent.com");
    }

    [Fact]
    public async Task RobotsTxt_IsServed()
    {
        var response = await _client.GetAsync("/robots.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("User-agent: *");
    }

    [Fact]
    public async Task Responses_AreMarkedNoindex_ToKeepTheApiHostOutOfSearch()
    {
        // Only the landing (trustscoreagent.com) should be indexed; the API host must not be.
        var response = await _client.GetAsync("/");

        response.Headers.GetValues("X-Robots-Tag").Should().Contain("noindex");
    }
}

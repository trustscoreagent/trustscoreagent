using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrustScore.Tests.Integration;

public class StatsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StatsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Stats_ReportRatingsAndRequestsByClient()
    {
        var client = ScoreEndpointTests.CreateTestClient(_factory);

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/score?service=api.example.com"))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "trustscoreagent-mcp/0.2.5");
            var scored = await client.SendAsync(request);
            scored.StatusCode.Should().Be(HttpStatusCode.OK);
            await scored.Content.ReadAsStringAsync();
        }
        await client.GetAsync("/health"); // 503 here (no database), and never counted either way

        var response = await client.GetAsync("/v1/stats?days=500");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("period").GetProperty("days").GetInt32().Should().Be(90, "the window is capped");
        root.GetProperty("ratings").GetProperty("from_probe").GetInt64().Should().Be(6);
        root.GetProperty("ratings").GetProperty("distinct_agents").GetInt64().Should().Be(2);
        root.GetProperty("ratings").GetProperty("with_verified_receipt").GetInt64().Should().Be(1);

        var requests = root.GetProperty("requests");
        requests.GetProperty("by_client").GetProperty("mcp/0.2.5").GetInt64().Should().Be(1);
        requests.GetProperty("by_endpoint").GetProperty("score").GetInt64().Should().Be(1);
        requests.GetProperty("by_endpoint").TryGetProperty("health", out _).Should().BeFalse(
            "health checks are not usage");
    }
}

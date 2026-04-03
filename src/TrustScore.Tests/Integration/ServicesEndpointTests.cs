using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrustScore.Tests.Integration;

public class ServicesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ServicesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = ScoreEndpointTests.CreateTestClient(factory);
    }

    [Fact]
    public async Task ListServices_ReturnsSeededService()
    {
        var response = await _client.GetAsync("/v1/services");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"services\"");
        body.Should().Contain("seeded.example.com");
        body.Should().Contain("\"pagination\"");
    }

    [Fact]
    public async Task ListServices_RespectsLimit()
    {
        var response = await _client.GetAsync("/v1/services?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"limit\":1");
    }

    [Fact]
    public async Task ListServices_InvalidSortBy_Returns400()
    {
        var response = await _client.GetAsync("/v1/services?sort_by=invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_sort_by");
    }

    [Fact]
    public async Task ListServices_FilterByMinScore()
    {
        // Seeded service has alpha=10, beta=2 → score ≈ 0.83
        var response = await _client.GetAsync("/v1/services?min_score=0.9");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        // Score 0.83 should be filtered out
        body.Should().Contain("\"count\":0");
    }

    [Fact]
    public async Task ListServices_FilterByMinRatings()
    {
        // Seeded service has 50 ratings
        var response = await _client.GetAsync("/v1/services?min_ratings=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"count\":0");
    }

    [Fact]
    public async Task ListServices_SortByRatingsCount()
    {
        var response = await _client.GetAsync("/v1/services?sort_by=ratings_count&order=desc");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("seeded.example.com");
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrustScore.Tests.Integration;

public class PremiumEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PremiumEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = ScoreEndpointTests.CreateTestClient(factory);
    }

    // --- History ---

    [Fact]
    public async Task History_KnownService_Returns200()
    {
        var response = await _client.GetAsync("/v1/score/history?did=did:web:seeded.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"service\"");
        body.Should().Contain("\"history\"");
        body.Should().Contain("\"current_score\"");
    }

    [Fact]
    public async Task History_UnknownService_Returns404()
    {
        var response = await _client.GetAsync("/v1/score/history?did=did:web:unknown.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_MissingDid_Returns400()
    {
        var response = await _client.GetAsync("/v1/score/history?did=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- Detailed ---

    [Fact]
    public async Task Detailed_KnownService_Returns200()
    {
        var response = await _client.GetAsync("/v1/score/detailed?did=did:web:seeded.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"dimensions\"");
        body.Should().Contain("\"quality_distribution\"");
        body.Should().Contain("\"receipt_stats\"");
    }

    [Fact]
    public async Task Detailed_UnknownService_Returns404()
    {
        var response = await _client.GetAsync("/v1/score/detailed?did=did:web:unknown.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Bulk ---

    [Fact]
    public async Task Bulk_ValidRequest_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/v1/scores/bulk", new
        {
            dids = new[] { "did:web:seeded.example.com", "did:web:unknown.example.com" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"requested\":2");
        body.Should().Contain("\"found\":1");
    }

    [Fact]
    public async Task Bulk_EmptyDids_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/v1/scores/bulk", new
        {
            dids = Array.Empty<string>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("missing_dids");
    }

    [Fact]
    public async Task Bulk_TooManyDids_Returns400()
    {
        var dids = Enumerable.Range(0, 101).Select(i => $"did:web:service{i}.example.com").ToList();
        var response = await _client.PostAsJsonAsync("/v1/scores/bulk", new { dids });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("too_many_dids");
    }
}

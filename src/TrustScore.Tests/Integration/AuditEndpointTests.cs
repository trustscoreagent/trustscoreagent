using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TrustScore.Tests.Integration;

public class AuditEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AuditEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = ScoreEndpointTests.CreateTestClient(factory);
    }

    [Fact]
    public async Task AuditRoot_NoAnchorsYet_Returns200WithNullRoot()
    {
        var response = await _client.GetAsync("/v1/audit/root");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"leaf_count\":0");
        body.Should().Contain("\"message\"");
    }

    [Fact]
    public async Task AuditRoot_ReturnsJsonContentType()
    {
        var response = await _client.GetAsync("/v1/audit/root");

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }
}

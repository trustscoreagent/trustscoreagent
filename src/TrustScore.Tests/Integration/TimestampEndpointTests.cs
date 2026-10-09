using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;
using Xunit;

namespace TrustScore.Tests.Integration;

public class TimestampEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly byte[] File = [0x00, 0x4f, 0x54, 0x53];
    private static readonly MerkleAnchor Stamped = new()
    {
        Id = 7,
        MerkleRoot = new string('a', 64),
        LeafCount = 3,
        TreeVersion = 2,
        OtsStatus = "bitcoin",
        OtsBitcoinHeight = 917_342,
    };
    private static readonly MerkleAnchor Unstamped = new() { Id = 6, MerkleRoot = new string('b', 64), LeafCount = 2, TreeVersion = 2 };

    private readonly HttpClient _client;

    public TimestampEndpointTests(WebApplicationFactory<Program> factory) =>
        _client = ScoreEndpointTests.CreateTestClient(factory, s =>
        {
            s.RemoveAll<IAuditService>();
            s.AddSingleton<IAuditService>(new StampedAuditService());
        });

    [Fact]
    public async Task OtsFile_IsServedForAStampedAnchor_And404Otherwise()
    {
        var response = await _client.GetAsync("/v1/audit/anchors/7/ots");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.opentimestamps.v1");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("trustscoreagent-anchor-7.ots");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(File);

        (await _client.GetAsync("/v1/audit/anchors/6/ots")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anchors_AndRoot_CarryTheirTimestampStatus()
    {
        using var anchors = JsonDocument.Parse(await _client.GetStringAsync("/v1/audit/anchors"));
        var list = anchors.RootElement.GetProperty("anchors");
        var ots = list[0].GetProperty("opentimestamps");
        ots.GetProperty("status").GetString().Should().Be("bitcoin");
        ots.GetProperty("bitcoin_block_height").GetInt32().Should().Be(917_342);
        ots.GetProperty("proof").GetString().Should().Be("/v1/audit/anchors/7/ots");
        list[1].GetProperty("opentimestamps").ValueKind.Should().Be(JsonValueKind.Null);

        using var root = JsonDocument.Parse(await _client.GetStringAsync("/v1/audit/root"));
        root.RootElement.GetProperty("opentimestamps").GetProperty("status").GetString().Should().Be("bitcoin");
    }

    private sealed class StampedAuditService : IAuditService
    {
        public Task<MerkleAnchor?> GetLatestAnchorAsync() => Task.FromResult<MerkleAnchor?>(Stamped);
        public Task<InclusionProofResult?> GetInclusionProofAsync(Guid ratingId) => Task.FromResult<InclusionProofResult?>(null);
        public Task<IReadOnlyList<MerkleAnchor>> GetAnchorsAsync(int limit, int? beforeId) =>
            Task.FromResult<IReadOnlyList<MerkleAnchor>>([Stamped, Unstamped]);
        public Task<ConsistencyProofResult> GetConsistencyProofAsync(int fromId, int toId) =>
            Task.FromResult(new ConsistencyProofResult { Status = ConsistencyProofStatus.AnchorNotFound });
        public Task<byte[]?> GetAnchorTimestampFileAsync(int anchorId) =>
            Task.FromResult(anchorId == Stamped.Id ? File : null);
    }
}

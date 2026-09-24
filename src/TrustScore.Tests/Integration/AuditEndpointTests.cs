using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;
using Xunit;

namespace TrustScore.Tests.Integration;

public class AuditEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public AuditEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
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

    [Fact]
    public async Task AuditProof_InvalidUuid_Returns400()
    {
        var response = await _client.GetAsync("/v1/audit/proof/not-a-uuid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_rating_id");
    }

    [Fact]
    public async Task AuditProof_NonExistentRating_Returns404()
    {
        var response = await _client.GetAsync($"/v1/audit/proof/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not_found");
    }

    private sealed class FixedProofAuditService(InclusionProofResult proof) : IAuditService
    {
        public Task<MerkleAnchor?> GetLatestAnchorAsync() => Task.FromResult<MerkleAnchor?>(null);
        public Task<InclusionProofResult?> GetInclusionProofAsync(Guid ratingId) =>
            Task.FromResult<InclusionProofResult?>(proof);
        public Task<IReadOnlyList<MerkleAnchor>> GetAnchorsAsync(int limit, int? beforeId) =>
            Task.FromResult<IReadOnlyList<MerkleAnchor>>(Array.Empty<MerkleAnchor>());
        public Task<ConsistencyProofResult> GetConsistencyProofAsync(int fromId, int toId) =>
            Task.FromResult(new ConsistencyProofResult { Status = ConsistencyProofStatus.AnchorNotFound });
    }

    [Fact]
    public async Task AuditProof_PublishesCommittedFields_ThatRebuildTheLeafPerTheSpec()
    {
        var leaves = Enumerable.Range(0, 3).Select(i => new RatingLeaf(
            Guid.NewGuid(), "api.example.com", new DateTimeOffset(2026, 9, 24, 16, 0, i, TimeSpan.Zero).AddTicks(12340),
            2, 200, 100 + i, null, true, null, false, false, true, 0.3 * 0.3405)).ToList();
        var tree = new MerkleTree(MerkleTreeVersion.V2);
        leaves.ForEach(l => tree.AddLeafHash(l.Hash()));
        var target = leaves[1];
        var result = new InclusionProofResult
        {
            RatingId = target.Id.ToString(),
            LeafHash = target.HashHex(),
            Leaf = target,
            TreeVersion = MerkleTreeVersion.V2,
            MerkleRoot = tree.RootHex!,
            Proof = tree.GetInclusionProof(1).Select(p => new ProofNodeDto(p.HashHex, p.IsRight)).ToList(),
            LeafIndex = 1,
            TotalLeaves = 3,
        };
        var client = ScoreEndpointTests.CreateTestClient(_factory, s =>
        {
            s.RemoveAll<IAuditService>();
            s.AddSingleton<IAuditService>(new FixedProofAuditService(result));
        });

        var json = JsonDocument.Parse(await client.GetStringAsync($"/v1/audit/proof/{target.Id}")).RootElement;

        json.GetProperty("leaf_version").GetInt32().Should().Be(2);
        json.GetProperty("tree_version").GetInt32().Should().Be(2);
        var c = json.GetProperty("committed");
        c.GetProperty("weight").GetString().Should().Be("0.102150");
        c.GetProperty("created_at").GetString().Should().Be("2026-09-24T16:00:01.001234Z");
        c.GetProperty("quality_score").ValueKind.Should().Be(JsonValueKind.Null);

        // Rebuild the canonical text from the JSON alone, following docs/MERKLE-SPEC.md.
        string Opt(JsonElement e, Func<JsonElement, string> f) => e.ValueKind == JsonValueKind.Null ? "" : f(e);
        string B(JsonElement e) => e.GetBoolean() ? "true" : "false";
        var canonical = string.Join('\n',
            "trustscore-leaf-v2",
            c.GetProperty("id").GetString(), c.GetProperty("service").GetString(), c.GetProperty("created_at").GetString(),
            c.GetProperty("status_code").GetInt32().ToString(), c.GetProperty("latency_ms").GetInt32().ToString(),
            Opt(c.GetProperty("response_size_bytes"), e => e.GetInt32().ToString()),
            Opt(c.GetProperty("schema_valid"), B),
            Opt(c.GetProperty("quality_score"), e => e.GetInt32().ToString()),
            B(c.GetProperty("has_receipt")), B(c.GetProperty("receipt_verified")), B(c.GetProperty("signature_verified")),
            c.GetProperty("weight").GetString());
        var rebuilt = SHA256.HashData(new byte[] { 0 }.Concat(Encoding.UTF8.GetBytes(canonical)).ToArray());

        Convert.ToHexString(rebuilt).ToLowerInvariant().Should().Be(json.GetProperty("leaf_hash").GetString());
    }

    private sealed class ConsistencyAuditService(ConsistencyProofResult result, IReadOnlyList<MerkleAnchor> anchors) : IAuditService
    {
        public Task<MerkleAnchor?> GetLatestAnchorAsync() => Task.FromResult<MerkleAnchor?>(null);
        public Task<InclusionProofResult?> GetInclusionProofAsync(Guid ratingId) => Task.FromResult<InclusionProofResult?>(null);
        public Task<IReadOnlyList<MerkleAnchor>> GetAnchorsAsync(int limit, int? beforeId) =>
            Task.FromResult<IReadOnlyList<MerkleAnchor>>(anchors.Where(a => beforeId is null || a.Id < beforeId).Take(limit).ToList());
        public Task<ConsistencyProofResult> GetConsistencyProofAsync(int fromId, int toId) => Task.FromResult(result);
    }

    private static readonly MerkleAnchor A1 = new() { Id = 1, MerkleRoot = "aa", LeafCount = 3, TreeVersion = 2 };
    private static readonly MerkleAnchor A2 = new() { Id = 2, MerkleRoot = "bb", LeafCount = 5, TreeVersion = 2 };

    private HttpClient ConsistencyClient(ConsistencyProofStatus status, params string[] proof) =>
        ScoreEndpointTests.CreateTestClient(_factory, s =>
        {
            s.RemoveAll<IAuditService>();
            s.AddSingleton<IAuditService>(new ConsistencyAuditService(
                new ConsistencyProofResult { Status = status, From = A1, To = A2, Proof = proof },
                new[] { A2, A1 }));
        });

    [Fact]
    public async Task Consistency_WithoutAnchorIds_Returns400()
        => (await _client.GetAsync("/v1/audit/consistency?from=1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Theory]
    [InlineData(ConsistencyProofStatus.AnchorNotFound, HttpStatusCode.NotFound)]
    [InlineData(ConsistencyProofStatus.Unsupported, HttpStatusCode.UnprocessableEntity)]
    [InlineData(ConsistencyProofStatus.NotConsistent, HttpStatusCode.Conflict)]
    [InlineData(ConsistencyProofStatus.SnapshotUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Consistency_MapsEveryFailureToItsOwnStatus(ConsistencyProofStatus status, HttpStatusCode expected)
        => (await ConsistencyClient(status).GetAsync("/v1/audit/consistency?from=1&to=2")).StatusCode.Should().Be(expected);

    [Fact]
    public async Task Consistency_Ok_ReturnsBothAnchorsAndTheProof()
    {
        var json = JsonDocument.Parse(await ConsistencyClient(ConsistencyProofStatus.Ok, "c1", "c2")
            .GetStringAsync("/v1/audit/consistency?from=1&to=2")).RootElement;

        json.GetProperty("from").GetProperty("leaf_count").GetInt32().Should().Be(3);
        json.GetProperty("to").GetProperty("merkle_root").GetString().Should().Be("bb");
        json.GetProperty("proof").EnumerateArray().Select(e => e.GetString()).Should().Equal("c1", "c2");
    }

    [Fact]
    public async Task Anchors_ArePaged_NewestFirst()
    {
        var client = ConsistencyClient(ConsistencyProofStatus.Ok);

        var page1 = JsonDocument.Parse(await client.GetStringAsync("/v1/audit/anchors?limit=1")).RootElement;
        page1.GetProperty("anchors")[0].GetProperty("id").GetInt32().Should().Be(2);
        page1.GetProperty("anchors")[0].GetProperty("tree_version").GetInt32().Should().Be(2);
        var next = page1.GetProperty("next_before").GetInt32();

        var page2 = JsonDocument.Parse(await client.GetStringAsync($"/v1/audit/anchors?limit=1&before={next}")).RootElement;
        page2.GetProperty("anchors")[0].GetProperty("id").GetInt32().Should().Be(1);
    }
}

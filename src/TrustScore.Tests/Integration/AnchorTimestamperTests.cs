using System.Net;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TrustScore.Api.Data;
using TrustScore.Api.Jobs;
using TrustScore.Core.Audit;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// The OpenTimestamps step of the batch job against a real migrated PostgreSQL database, with the
/// calendars replaced by canned answers (the real ones captured in <see cref="OtsVectors"/>).
/// </summary>
public class AnchorTimestamperTests : PostgresDatabaseTest
{
    private static readonly OpenTimestampsOptions Options = new()
    {
        Calendars = ["https://a.pool.opentimestamps.org", "https://b.pool.opentimestamps.org", "https://a.pool.eternitywall.com"],
    };

    private async Task<int> InsertAnchorAsync(int treeVersion = 2, string? root = null)
    {
        using var conn = Db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO merkle_anchors (merkle_root, leaf_count, tree_version, anchored_at)
            VALUES (@Root, 3, @Version, NOW())
            RETURNING id
            """,
            new { Root = root ?? OtsVectors.Digest, Version = (short)treeVersion });
    }

    private async Task<(string? Status, int? Height, byte[]? Proof)> ReadAsync(int id)
    {
        using var conn = Db.CreateConnection();
        return await conn.QuerySingleAsync<(string?, int?, byte[]?)>(
            "SELECT ots_status, ots_bitcoin_height, ots_proof FROM merkle_anchors WHERE id = @Id", new { Id = id });
    }

    private AnchorTimestamper Timestamper(Calendars calendars) =>
        new(Db, calendars, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<AnchorTimestamper>.Instance);

    [PostgresFact]
    public async Task NewAnchor_IsStampedByTheCalendarsThatAnswer_ThenUpgradedToBitcoin()
    {
        var id = await InsertAnchorAsync();
        var legacy = await InsertAnchorAsync(treeVersion: 1, root: new string('a', 64));
        var calendars = new Calendars
        {
            ["POST https://a.pool.opentimestamps.org/digest"] = _ => Ok(OtsVectors.AlicePool),
            ["POST https://b.pool.opentimestamps.org/digest"] = _ => Ok(OtsVectors.BobPool),
            // The third calendar is down: the other two are enough.
            ["POST https://a.pool.eternitywall.com/digest"] = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };

        await Timestamper(calendars).RunAsync();

        var stamped = await ReadAsync(id);
        stamped.Status.Should().Be("pending");
        var proof = OtsTimestamp.Parse(stamped.Proof);
        proof.Attestations(OtsVectors.Bytes(OtsVectors.Digest)).Select(a => a.Attestation.CalendarUrl).Should()
            .BeEquivalentTo("https://alice.btc.calendar.opentimestamps.org", "https://bob.btc.calendar.opentimestamps.org");
        (await ReadAsync(legacy)).Status.Should().BeNull("v1 anchors are not stamped");
        calendars.Requests.Should().OnlyContain(r => r.StartsWith("POST "), "a fresh proof is not upgraded yet");

        // Served as a .ots file over the root.
        var audit = new AuditService(Db, new RatingRepository(Db), new MemoryCache(new MemoryCacheOptions()));
        var file = await audit.GetAnchorTimestampFileAsync(id);
        file.Should().NotBeNull();
        file!.AsSpan(33, 32).ToArray().Should().Equal(OtsVectors.Bytes(OtsVectors.Digest));
        (await audit.GetAnchorTimestampFileAsync(legacy)).Should().BeNull();
        var listed = await audit.GetAnchorsAsync(10, null);
        listed.Single(a => a.Id == id).OtsStatus.Should().Be("pending");
        listed.Single(a => a.Id == legacy).OtsStatus.Should().BeNull();

        // Hours later, alice has committed the root into Bitcoin; bob has not yet.
        using (var conn = Db.CreateConnection())
            await conn.ExecuteAsync("UPDATE merkle_anchors SET anchored_at = NOW() - INTERVAL '3 hours'");
        var aliceMessage = OtsVectors.PendingMessages["https://alice.btc.calendar.opentimestamps.org"];
        var bobMessage = OtsVectors.PendingMessages["https://bob.btc.calendar.opentimestamps.org"];
        var upgrade = new OtsTimestamp();
        var leaf = new OtsTimestamp();
        leaf.Items.Add(OtsAttestation.Bitcoin(917_342));
        upgrade.Items.Add(new OtsOp(OtsOp.Sha256, null, leaf));
        calendars[$"GET https://alice.btc.calendar.opentimestamps.org/timestamp/{aliceMessage}"] =
            _ => Ok(Convert.ToHexString(upgrade.Serialize()));
        calendars[$"GET https://bob.btc.calendar.opentimestamps.org/timestamp/{bobMessage}"] =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        await Timestamper(calendars).RunAsync();

        var upgraded = await ReadAsync(id);
        upgraded.Status.Should().Be("bitcoin");
        upgraded.Height.Should().Be(917_342);
        var attestations = OtsTimestamp.Parse(upgraded.Proof).Attestations(OtsVectors.Bytes(OtsVectors.Digest)).ToList();
        attestations.Should().Contain(a => a.Attestation.BitcoinHeight == 917_342);
        attestations.Should().Contain(a => a.Attestation.CalendarUrl == "https://bob.btc.calendar.opentimestamps.org",
            "bob's branch stays pending, for a later upgrade or for the verifier");
    }

    [PostgresFact]
    public async Task AllCalendarsDown_LeavesTheAnchorForTheNextRun_WithoutFailing()
    {
        var id = await InsertAnchorAsync();
        var calendars = new Calendars(); // every request throws, like an unreachable host

        await Timestamper(calendars).RunAsync();

        (await ReadAsync(id)).Status.Should().BeNull();
        calendars.Requests.Should().HaveCount(3);
    }

    [PostgresFact]
    public async Task StoredProof_PointingAnywhereElse_IsNeverFollowed()
    {
        var id = await InsertAnchorAsync();
        var hostile = new OtsTimestamp();
        hostile.Items.Add(OtsAttestation.Pending("http://169.254.169.254"));
        hostile.Items.Add(OtsAttestation.Pending("https://calendar.opentimestamps.org.evil.example"));
        using (var conn = Db.CreateConnection())
            await conn.ExecuteAsync(
                """
                UPDATE merkle_anchors
                SET ots_proof = @Proof, ots_status = 'pending', anchored_at = NOW() - INTERVAL '3 hours'
                WHERE id = @Id
                """,
                new { Proof = hostile.Serialize(), Id = id });
        var calendars = new Calendars();

        await Timestamper(calendars).RunAsync();

        calendars.Requests.Should().BeEmpty();
        (await ReadAsync(id)).Status.Should().Be("pending");
    }

    private static HttpResponseMessage Ok(string hex) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Convert.FromHexString(hex)),
    };

    /// <summary>Canned calendar answers keyed by "METHOD url"; anything else fails like an unreachable host.</summary>
    private sealed class Calendars : Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(Calendars calendars) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var key = $"{request.Method} {request.RequestUri}";
                lock (calendars.Requests) calendars.Requests.Add(key);
                return calendars.TryGetValue(key, out var answer)
                    ? Task.FromResult(answer(request))
                    : throw new HttpRequestException($"No route to {request.RequestUri}");
            }
        }
    }
}

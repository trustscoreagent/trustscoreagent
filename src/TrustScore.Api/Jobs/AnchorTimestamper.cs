using System.Net;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Extensions.Options;
using TrustScore.Api.Data;
using TrustScore.Api.Logging;
using TrustScore.Core.Audit;

namespace TrustScore.Api.Jobs;

public sealed class OpenTimestampsOptions
{
    public const string SectionName = "OpenTimestamps";

    public bool Enabled { get; set; } = true;

    /// <summary>Calendars a new root is submitted to; one answer is enough, all of them is better.</summary>
    public string[] Calendars { get; set; } =
    [
        "https://a.pool.opentimestamps.org",
        "https://b.pool.opentimestamps.org",
        "https://a.pool.eternitywall.com",
    ];

    /// <summary>
    /// Hosts a pending attestation may point to for its upgrade. The pools above answer with the
    /// calendar that will commit the root (alice.btc.calendar.opentimestamps.org, ...), and the
    /// upgrade must only ever call those, never a URL some stored proof happens to contain.
    /// </summary>
    public string[] UpgradeHostSuffixes { get; set; } =
    [
        "calendar.opentimestamps.org",
        "calendar.eternitywall.com",
        "calendar.catallaxy.com",
    ];

    /// <summary>Anchors stamped, and pending proofs upgraded, per run.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>A calendar commits to Bitcoin every few hours; asking sooner is wasted.</summary>
    public int UpgradeAfterHours { get; set; } = 2;
}

/// <summary>
/// Timestamps the anchored Merkle roots with OpenTimestamps: submits each new root to public
/// calendars, stores the pending proof, and on later runs swaps it for the completed Bitcoin proof.
/// Calendars being slow or down only delays this (logged as a warning and retried next run); it
/// never fails the batch job.
/// </summary>
public sealed class AnchorTimestamper
{
    public const string HttpClientName = "opentimestamps";
    private static readonly MediaTypeWithQualityHeaderValue OtsMediaType = new("application/vnd.opentimestamps.v1");

    private readonly DbConnectionFactory _db;
    private readonly IHttpClientFactory _http;
    private readonly OpenTimestampsOptions _options;
    private readonly ILogger<AnchorTimestamper> _logger;

    public AnchorTimestamper(
        DbConnectionFactory db, IHttpClientFactory http, IOptions<OpenTimestampsOptions> options,
        ILogger<AnchorTimestamper> logger)
    {
        _db = db;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("OpenTimestamps: disabled");
            return;
        }
        await StampNewAnchorsAsync();
        await UpgradePendingAsync();
    }

    // Positional: Dapper binds it by constructor, so column order and exact types matter.
    internal sealed record AnchorRow(int Id, string MerkleRoot, byte[]? OtsProof);

    private async Task StampNewAnchorsAsync()
    {
        using var conn = _db.CreateConnection();
        var anchors = (await conn.QueryAsync<AnchorRow>(
            """
            SELECT id AS Id, merkle_root AS MerkleRoot, ots_proof AS OtsProof
            FROM merkle_anchors
            WHERE ots_proof IS NULL AND tree_version = 2
            ORDER BY id DESC
            LIMIT @Limit
            """,
            new { Limit = _options.BatchSize })).ToList();

        foreach (var anchor in anchors)
        {
            var digest = Convert.FromHexString(anchor.MerkleRoot);
            var answers = await Task.WhenAll(_options.Calendars.Select(c => SubmitAsync(c, digest)));
            var stamped = answers.OfType<OtsTimestamp>().ToList();
            if (stamped.Count == 0)
            {
                _logger.LogWarning("OpenTimestamps: no calendar stamped anchor {Id}; retrying next run", anchor.Id);
                continue;
            }

            var proof = OtsTimestamp.Merge(stamped).Serialize();
            await conn.ExecuteAsync(
                """
                UPDATE merkle_anchors
                SET ots_proof = @Proof, ots_status = 'pending', ots_updated_at = NOW()
                WHERE id = @Id AND ots_proof IS NULL
                """,
                new { Proof = proof, anchor.Id });
            _logger.LogInformation("OpenTimestamps: anchor {Id} stamped by {Count}/{Total} calendars",
                anchor.Id, stamped.Count, _options.Calendars.Length);
        }
    }

    private async Task UpgradePendingAsync()
    {
        using var conn = _db.CreateConnection();
        var anchors = (await conn.QueryAsync<AnchorRow>(
            """
            SELECT id AS Id, merkle_root AS MerkleRoot, ots_proof AS OtsProof
            FROM merkle_anchors
            WHERE ots_status = 'pending' AND anchored_at < NOW() - make_interval(hours => @Hours)
            ORDER BY id
            LIMIT @Limit
            """,
            new { Hours = _options.UpgradeAfterHours, Limit = _options.BatchSize })).ToList();

        foreach (var anchor in anchors)
        {
            var digest = Convert.FromHexString(anchor.MerkleRoot);
            var timestamp = OtsTimestamp.Parse(anchor.OtsProof!);

            // Fetch first (async), then splice in (sync): one request per pending attestation.
            var upgrades = new Dictionary<string, OtsTimestamp>();
            foreach (var (attestation, message) in timestamp.Attestations(digest))
            {
                if (attestation.CalendarUrl is not { } calendar || !IsUpgradeCalendar(calendar))
                    continue;
                if (await FetchUpgradeAsync(calendar, message) is { } upgraded)
                    upgrades[UpgradeKey(calendar, message)] = upgraded;
            }
            if (upgrades.Count == 0)
                continue;

            timestamp.ReplacePending(digest, (attestation, message) =>
                upgrades.GetValueOrDefault(UpgradeKey(attestation.CalendarUrl!, message)));

            var heights = timestamp.Attestations(digest)
                .Select(a => a.Attestation.BitcoinHeight)
                .OfType<long>()
                .ToList();
            await conn.ExecuteAsync(
                """
                UPDATE merkle_anchors
                SET ots_proof = @Proof, ots_status = @Status, ots_bitcoin_height = @Height, ots_updated_at = NOW()
                WHERE id = @Id
                """,
                new
                {
                    Proof = timestamp.Serialize(),
                    Status = heights.Count > 0 ? "bitcoin" : "pending",
                    Height = heights.Count > 0 ? (int?)heights.Min() : null,
                    anchor.Id,
                });
            _logger.LogInformation("OpenTimestamps: anchor {Id} upgraded ({Count} calendar(s)), Bitcoin block {Height}",
                anchor.Id, upgrades.Count, heights.Count > 0 ? heights.Min() : null);
        }
    }

    private static string UpgradeKey(string calendar, byte[] message) => calendar + "|" + Convert.ToHexString(message);

    internal bool IsUpgradeCalendar(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && _options.UpgradeHostSuffixes.Any(s =>
            uri.Host.Equals(s, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    private async Task<OtsTimestamp?> SubmitAsync(string calendar, byte[] digest)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{calendar.TrimEnd('/')}/digest")
            {
                Content = new ByteArrayContent(digest),
            };
            request.Headers.Accept.Add(OtsMediaType);
            var timestamp = await SendAsync(request);
            if (timestamp is null)
                return null;

            // Keep only answers that end where they should: a calendar we would later upgrade from.
            if (!timestamp.Attestations(digest).Any(a => a.Attestation.CalendarUrl is { } url && IsUpgradeCalendar(url)))
            {
                _logger.LogWarning("OpenTimestamps: {Calendar} answered without a known calendar attestation", LogValue.Safe(calendar));
                return null;
            }
            return timestamp;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or NotSupportedException)
        {
            _logger.LogWarning("OpenTimestamps: {Calendar} failed to stamp: {Error}", LogValue.Safe(calendar), ex.Message);
            return null;
        }
    }

    private async Task<OtsTimestamp?> FetchUpgradeAsync(string calendar, byte[] message)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{calendar.TrimEnd('/')}/timestamp/{Convert.ToHexString(message).ToLowerInvariant()}");
            request.Headers.Accept.Add(OtsMediaType);
            return await SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            _logger.LogWarning("OpenTimestamps: upgrade from {Calendar} failed: {Error}", LogValue.Safe(calendar), ex.Message);
            return null;
        }
    }

    /// <summary>The parsed timestamp of a 200 answer; null for 404 (not committed yet) or any other status.</summary>
    private async Task<OtsTimestamp?> SendAsync(HttpRequestMessage request)
    {
        var client = _http.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode != HttpStatusCode.OK)
            return null;
        if (response.Content.Headers.ContentLength > OtsTimestamp.MaxProofBytes)
            throw new FormatException("Calendar answer too large.");

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[OtsTimestamp.MaxProofBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length))) > 0)
            length += read;
        if (length > OtsTimestamp.MaxProofBytes)
            throw new FormatException("Calendar answer too large.");
        return OtsTimestamp.Parse(buffer.AsSpan(0, length));
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using TrustScore.Core.X402;

namespace TrustScore.Api.Jobs;

/// <summary>
/// Measures the x402 endpoints agents actually pay for. Once a day it picks them from the public
/// Bazaar catalog (endpoints with real payers in the last 30 days, a few per host); every pass it
/// sends each one request <b>without payment</b> and records what came back as an ordinary
/// rating: availability (a valid 402 challenge counts as the endpoint answering), latency, and
/// conformity (whether that challenge is a complete, parseable x402 one).
///
/// The same honesty rules as <see cref="SeedProber"/>: real measurements only, one transparent
/// probe identity at normal unverified weight, quarantine for targets that fail for days. It never
/// pays and holds no wallet or key.
/// </summary>
public sealed class X402Prober
{
    public const string HttpClientName = "x402-probe";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IScoringEngine _scoring;
    private readonly IRatingWriter _ratingWriter;
    private readonly IAgentRepository _agentRepo;
    private readonly IProbeHealthRepository _healthRepo;
    private readonly IX402TargetRepository _targets;
    private readonly X402ProbeOptions _options;
    private readonly ILogger<X402Prober> _logger;

    public X402Prober(
        IHttpClientFactory httpClientFactory,
        IScoringEngine scoring,
        IRatingWriter ratingWriter,
        IAgentRepository agentRepo,
        IProbeHealthRepository healthRepo,
        IX402TargetRepository targets,
        IOptions<X402ProbeOptions> options,
        ILogger<X402Prober> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scoring = scoring;
        _ratingWriter = ratingWriter;
        _agentRepo = agentRepo;
        _healthRepo = healthRepo;
        _targets = targets;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("X402Probe: disabled, skipping");
            return;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        await RefreshSelectionIfDueAsync(client);

        var targets = await _targets.GetActiveAsync();
        if (targets.Count == 0)
        {
            _logger.LogInformation("X402Probe: no targets selected yet, skipping");
            return;
        }

        var agentTrust = await _agentRepo.GetTrustScoreAsync(
            TrustIdentity.For(_options.AgentDid, signatureVerified: false));
        var tracker = await ProbeHealthTracker.LoadAsync(
            _healthRepo, _options.QuarantineAfterConsecutiveFailures, _options.QuarantineAfter, _logger, "X402Probe");

        int recorded = 0, errors = 0, suppressed = 0, challenges = 0;
        await Parallel.ForEachAsync(
            SpreadByHost(targets),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrentProbes },
            async (target, _) =>
            {
                try
                {
                    var (statusCode, latencyMs, schemaValid) = await ProbeAsync(client, target);
                    if (schemaValid == true) Interlocked.Increment(ref challenges);

                    // A moved or retired endpoint answers 404 forever; any 402, valid or not, shows the
                    // endpoint is there. An invalid challenge must keep being published, not end up
                    // quarantined: it is precisely what a payer needs to know.
                    var usable = statusCode == 402 || statusCode is >= 200 and < 300;
                    if (!await tracker.RecordAsync(target.ServiceDid, usable, statusCode, target.Url, DateTimeOffset.UtcNow))
                    {
                        Interlocked.Increment(ref suppressed);
                        return;
                    }

                    var rating = new Rating
                    {
                        ServiceDid = target.ServiceDid,
                        AgentDid = _options.AgentDid,
                        Metrics = new RatingMetrics
                        {
                            StatusCode = statusCode,
                            LatencyMs = latencyMs,
                            SchemaValid = schemaValid,
                        },
                        HasReceipt = false,
                        ReceiptVerified = false,
                        Weight = SeedProber.SeedProbeBaseWeight * agentTrust,
                    };
                    await _ratingWriter.SubmitAsync(target.ServiceDid, _scoring.ComputeDelta(rating), rating);
                    Interlocked.Increment(ref recorded);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    _logger.LogWarning(ex, "X402Probe: failed to record rating for {Service}", target.ServiceDid);
                }
            });

        _logger.LogInformation(
            "X402Probe complete: {Targets} targets, {Recorded} recorded ({Challenges} valid challenges), " +
            "{Suppressed} suppressed (quarantined), {Errors} errors",
            targets.Count, recorded, challenges, suppressed, errors);
    }

    private async Task RefreshSelectionIfDueAsync(HttpClient client)
    {
        var last = await _targets.GetLastRefreshAsync();
        var now = DateTimeOffset.UtcNow;
        if (last is { } l && now - l < _options.RefreshInterval)
            return;

        IReadOnlyList<X402CatalogEntry> catalog;
        try
        {
            catalog = await FetchCatalogAsync(client);
        }
        catch (Exception ex)
        {
            // The catalog is in early development and may change or be down; the previous
            // selection stays in use until a refresh succeeds.
            _logger.LogWarning(ex, "X402Probe: could not read the Bazaar catalog, keeping the current selection");
            return;
        }

        var selection = SelectTargets(
            catalog, _options.MinUniquePayers, _options.MaxPerHost, _options.MaxTargets, _options.ExcludedHosts);
        if (selection.Count == 0)
        {
            _logger.LogWarning(
                "X402Probe: the catalog ({Entries} entries) yielded no target, keeping the current selection",
                catalog.Count);
            return;
        }

        await _targets.ReplaceSelectionAsync(selection, now);
        _logger.LogInformation(
            "X402Probe: selected {Selected} endpoints on {Hosts} hosts from {Entries} catalog entries",
            selection.Count, selection.Select(t => ServiceIdentifier.ExtractProvider(t.ServiceDid)).Distinct().Count(),
            catalog.Count);
    }

    private async Task<IReadOnlyList<X402CatalogEntry>> FetchCatalogAsync(HttpClient client)
    {
        var entries = new List<X402CatalogEntry>();
        for (var page = 0; page < _options.MaxCatalogPages; page++)
        {
            if (page > 0) await Task.Delay(_options.CatalogPageDelay);

            var url = $"{_options.CatalogUrl}?limit={_options.CatalogPageSize}&offset={page * _options.CatalogPageSize}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.CatalogTimeoutSeconds));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var json = await SeedProber.ReadBoundedAsync(stream, MaxCatalogPageBytes, cts.Token);

            var (items, total) = ParseCatalogPage(json);
            entries.AddRange(items);
            if (items.Count == 0 || entries.Count >= total)
                return entries;
        }
        // Stopping here is safe: the selection is made from what was read.
        _logger.LogWarning("X402Probe: catalog longer than {Pages} pages, read {Entries} entries",
            _options.MaxCatalogPages, entries.Count);
        return entries;
    }

    /// <summary>
    /// One page of <c>GET /platform/v2/x402/discovery/resources</c>: the entries it lists and the
    /// catalog's total. Entries without a usable resource URL are dropped.
    /// </summary>
    internal static (IReadOnlyList<X402CatalogEntry> Items, int Total) ParseCatalogPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("pagination", out var p) && p.TryGetProperty("total", out var t)
                    && t.TryGetInt32(out var n) ? n : 0;

        var items = new List<X402CatalogEntry>();
        if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("resource", out var r) || r.ValueKind != JsonValueKind.String)
                    continue;
                var type = item.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
                var payers = item.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object
                             && q.TryGetProperty("l30DaysUniquePayers", out var u) && u.TryGetInt32(out var up) ? up : 0;
                string? method = null;
                if (item.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object
                    && ext.TryGetProperty("bazaar", out var bz) && bz.ValueKind == JsonValueKind.Object
                    && bz.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                    && info.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                    && input.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                    method = m.GetString();
                items.Add(new X402CatalogEntry(r.GetString()!, type, method, payers));
            }
        }
        return (items, total);
    }

    /// <summary>
    /// The endpoints to probe: HTTPS resources of type http with more than
    /// <paramref name="minUniquePayers"/> distinct payers over 30 days, the most used first, at
    /// most <paramref name="maxPerHost"/> per host (so one provider listing thousands of
    /// endpoints cannot crowd out the rest) and <paramref name="maxTargets"/> in all. Resources
    /// that normalize to the same service id are probed once. Hosts in
    /// <paramref name="excludedHosts"/> (operators who asked not to be probed), and their
    /// subdomains, are left out.
    /// </summary>
    internal static IReadOnlyList<X402ProbeTarget> SelectTargets(
        IReadOnlyList<X402CatalogEntry> catalog, int minUniquePayers, int maxPerHost, int maxTargets,
        IReadOnlyCollection<string>? excludedHosts = null)
    {
        var excluded = (excludedHosts ?? [])
            .Select(h => h.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(h => h.Length > 0)
            .ToList();

        var perHost = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<X402ProbeTarget>();

        foreach (var entry in catalog
                     .Where(e => e.UniquePayers > minUniquePayers && (e.Type is null or "http"))
                     .OrderByDescending(e => e.UniquePayers)
                     .ThenBy(e => e.Resource, StringComparer.Ordinal))
        {
            if (selected.Count >= maxTargets) break;
            if (!Uri.TryCreate(entry.Resource, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0
                || entry.Resource.Length > 2048)
                continue;

            var method = (entry.Method ?? "GET").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "HEAD")) continue;

            var serviceId = ServiceIdentifier.Normalize(entry.Resource);
            var host = ServiceIdentifier.ExtractProvider(serviceId);
            if (excluded.Any(e => host == e || host.EndsWith("." + e, StringComparison.Ordinal))) continue;
            if (!seen.Add(serviceId)) continue;
            if (perHost.GetValueOrDefault(host) >= maxPerHost) continue;
            perHost[host] = perHost.GetValueOrDefault(host) + 1;

            selected.Add(new X402ProbeTarget(serviceId, entry.Resource, method, entry.UniquePayers));
        }
        return selected;
    }

    /// <summary>Interleaves hosts so a pass never sends one provider many requests at once.</summary>
    internal static IEnumerable<X402ProbeTarget> SpreadByHost(IReadOnlyList<X402ProbeTarget> targets)
    {
        var queues = targets
            .GroupBy(t => ServiceIdentifier.ExtractProvider(t.ServiceDid))
            .Select(g => new Queue<X402ProbeTarget>(g))
            .ToList();
        while (queues.Count > 0)
        {
            foreach (var q in queues) yield return q.Dequeue();
            queues.RemoveAll(q => q.Count == 0);
        }
    }

    private async Task<(int StatusCode, int LatencyMs, bool? SchemaValid)> ProbeAsync(HttpClient client, X402ProbeTarget target)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var request = new HttpRequestMessage(new HttpMethod(target.Method), target.Url);
            if (target.Method == "POST")
                // An empty JSON object: x402 servers answer 402 before reading the body, and
                // nothing is sent that could be mistaken for a real call.
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var body = await SeedProber.ReadBoundedAsync(stream, MaxProbeBodyBytes, cts.Token);
            sw.Stop();

            var status = (int)response.StatusCode;
            var latency = SeedProber.ClampLatency(sw.ElapsedMilliseconds);
            bool? schemaValid = status == 402
                ? X402Challenge.Parse(HeaderValue(response, X402Challenge.HeaderName), body) is not null
                // Any other answer says nothing about the challenge: conformity is unknown.
                : null;
            return (status, latency, schemaValid);
        }
        catch (Exception)
        {
            // Unreachable: recorded as a real availability failure, with the full timeout as the
            // latency so a fast refusal is not rewarded, and conformity unknown.
            return (503, SeedProber.ClampLatency(_options.TimeoutSeconds * 1000L), null);
        }
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()
        : response.Content.Headers.TryGetValues(name, out var cv) ? cv.FirstOrDefault()
        : null;

    // A 402 challenge or a short error is all a probe needs to read.
    private const int MaxProbeBodyBytes = 64 * 1024;

    // A catalog page of 1,000 entries is about 3.5 MB.
    private const int MaxCatalogPageBytes = 32 * 1024 * 1024;
}

/// <summary>One entry of the Bazaar catalog, reduced to what the selection uses.</summary>
public sealed record X402CatalogEntry(string Resource, string? Type, string? Method, int UniquePayers);

public sealed class X402ProbeOptions
{
    public const string SectionName = "X402Probe";

    public bool Enabled { get; set; }

    /// <summary>Same transparent identity as the seed probe: the registry's one probe agent.</summary>
    public string AgentDid { get; set; } = "did:web:trustscoreagent.com:probe";

    public string CatalogUrl { get; set; } = "https://api.cdp.coinbase.com/platform/v2/x402/discovery/resources";
    public int CatalogPageSize { get; set; } = 1000;
    public int MaxCatalogPages { get; set; } = 100;
    public TimeSpan CatalogPageDelay { get; set; } = TimeSpan.FromSeconds(1);
    public int CatalogTimeoutSeconds { get; set; } = 60;

    /// <summary>How often the selection is redone from the catalog.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(20);

    /// <summary>An endpoint is selected when it had MORE than this many distinct payers in 30 days.</summary>
    public int MinUniquePayers { get; set; } = 10;
    public int MaxPerHost { get; set; } = 20;
    public int MaxTargets { get; set; } = 500;

    /// <summary>Hosts whose operators asked not to be probed (their subdomains included).</summary>
    public List<string> ExcludedHosts { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 10;
    public int MaxConcurrentProbes { get; set; } = 8;

    public TimeSpan QuarantineAfter { get; set; } = TimeSpan.FromDays(3);
    public int QuarantineAfterConsecutiveFailures { get; set; } = 3;
}

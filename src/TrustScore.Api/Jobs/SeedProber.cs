using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Jobs;

/// <summary>
/// Establishes a real, auditable baseline for the registry by actively probing a curated
/// allowlist of public, free, unauthenticated APIs and recording the measured availability,
/// latency and conformity as ordinary ratings.
///
/// Honesty guarantees: every rating is a real HTTP measurement (never fabricated), submitted
/// under a single, transparent probe agent DID (not faked as multiple independent agents), with
/// no receipt — so it carries normal unverified weight (0.3 x the probe's EigenTrust score).
/// Community and receipt-verified ratings accumulate on top and take over.
/// </summary>
public sealed class SeedProber
{
    public const string HttpClientName = "seed-probe";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IScoringEngine _scoring;
    private readonly IRatingWriter _ratingWriter;
    private readonly IAgentRepository _agentRepo;
    private readonly SeedProbeOptions _options;
    private readonly ILogger<SeedProber> _logger;

    public SeedProber(
        IHttpClientFactory httpClientFactory,
        IScoringEngine scoring,
        IRatingWriter ratingWriter,
        IAgentRepository agentRepo,
        IOptions<SeedProbeOptions> options,
        ILogger<SeedProber> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scoring = scoring;
        _ratingWriter = ratingWriter;
        _agentRepo = agentRepo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SeedProbe: disabled, skipping");
            return;
        }
        if (_options.Targets.Count == 0)
        {
            _logger.LogInformation("SeedProbe: no targets configured, skipping");
            return;
        }

        // The probe rates as a single transparent agent; its weight is the normal unverified
        // weight scaled by its own EigenTrust score (no special privilege).
        var agentTrust = await _agentRepo.GetTrustScoreAsync(_options.AgentDid);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        int recorded = 0, errors = 0;
        foreach (var target in _options.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Service) || string.IsNullOrWhiteSpace(target.Url))
                continue;

            try
            {
                var (statusCode, latencyMs, schemaValid) = await ProbeAsync(client, target);
                var serviceId = ServiceIdentifier.Normalize(target.Service);

                var rating = new Rating
                {
                    ServiceDid = serviceId,
                    AgentDid = _options.AgentDid,
                    Metrics = new RatingMetrics
                    {
                        StatusCode = statusCode,
                        LatencyMs = latencyMs,
                        SchemaValid = schemaValid,
                    },
                    HasReceipt = false,
                    ReceiptVerified = false,
                    Weight = SeedProbeBaseWeight * agentTrust,
                };

                var delta = _scoring.ComputeDelta(rating);
                await _ratingWriter.SubmitAsync(serviceId, delta, rating);
                recorded++;
                _logger.LogInformation(
                    "SeedProbe {Service}: HTTP {Status} in {Latency}ms, schema_valid={Schema}",
                    serviceId, statusCode, latencyMs, schemaValid);
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogWarning(ex, "SeedProbe: failed to record rating for {Service}", target.Service);
            }
        }

        _logger.LogInformation("SeedProbe complete: {Recorded} recorded, {Errors} errors", recorded, errors);
    }

    // Same base weight as any rating without a receipt (spec §5).
    private const double SeedProbeBaseWeight = 0.3;

    private async Task<(int StatusCode, int LatencyMs, bool SchemaValid)> ProbeAsync(HttpClient client, SeedProbeTarget target)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await client.GetAsync(target.Url, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            var latency = ClampLatency(sw.ElapsedMilliseconds);
            var schemaValid = ValidateBody(body, target.ExpectField);
            return ((int)response.StatusCode, latency, schemaValid);
        }
        catch (Exception)
        {
            // Timeout / DNS / connection failure → the service was unreachable. Record it as a
            // real availability failure (503-equivalent) rather than swallowing it.
            sw.Stop();
            return (503, ClampLatency(sw.ElapsedMilliseconds), false);
        }
    }

    private static int ClampLatency(long elapsedMs) => (int)Math.Clamp(elapsedMs, 1, 600_000);

    /// <summary>
    /// Conformity check. With an <c>ExpectField</c> dot-path, the body must be JSON containing it
    /// (descending into the first element of an array root). Without one, a non-empty body counts
    /// (covers plain-text endpoints); a JSON array root must be non-empty.
    /// </summary>
    private static bool ValidateBody(string body, string? expectField)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        if (string.IsNullOrWhiteSpace(expectField))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => doc.RootElement.GetArrayLength() > 0,
                    JsonValueKind.Object => true,
                    _ => true,
                };
            }
            catch (JsonException)
            {
                // Non-JSON (e.g. plain text): a non-empty body is enough.
                return true;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var element = doc.RootElement;
            foreach (var part in expectField.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    if (element.GetArrayLength() == 0) return false;
                    element = element[0];
                }
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out var next))
                    return false;
                element = next;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class SeedProbeOptions
{
    public const string SectionName = "SeedProbe";

    public bool Enabled { get; set; }
    public string AgentDid { get; set; } = "did:web:probe.trustscoreagent.com";
    public int TimeoutSeconds { get; set; } = 10;
    public List<SeedProbeTarget> Targets { get; set; } = new();
}

public sealed class SeedProbeTarget
{
    /// <summary>Registry identifier (domain/URL/DID — normalized internally).</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Safe, idempotent GET endpoint to probe.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional JSON dot-path that must be present for the response to count as conformant.</summary>
    public string? ExpectField { get; set; }
}

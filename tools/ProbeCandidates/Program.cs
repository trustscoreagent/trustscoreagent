using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrustScore.Api.Jobs;
using TrustScore.Core.Models;

// Verifies seed-probe targets before they are allowed into appsettings.
//
// A bad target is expensive in a way a bad unit test is not: the probe will publish its failures
// as evidence about a real third party. The three ways that happens are an endpoint that needs a
// key (answers 401 forever, scored as an outage), a URL that redirects (the probe refuses
// redirects, so it never gets a body), and an ExpectField that is not actually in the response
// (conformity reported false on every pass). All three look fine to a browser and to curl.
//
// So this runs the probe's real HTTP configuration and its real ValidateBody, and rejects
// anything it would not have scored cleanly.
//
//   dotnet run --project tools/ProbeCandidates                    # audit the current config
//   dotnet run --project tools/ProbeCandidates -- candidates.json # check new candidates

var candidatesPath = args.Length > 0 ? args[0] : null;
var repoRoot = FindRepoRoot();
var appsettingsPath = Path.Combine(repoRoot, "src", "TrustScore.Api", "appsettings.json");

List<Candidate> candidates;
if (candidatesPath is null)
{
    candidates = LoadConfiguredTargets(appsettingsPath);
    Console.WriteLine($"Auditing the {candidates.Count} targets already in appsettings.json.\n");
}
else
{
    candidates = LoadCandidates(candidatesPath);
    Console.WriteLine($"Checking {candidates.Count} candidates from {candidatesPath}.\n");
}

var existing = LoadConfiguredTargets(appsettingsPath)
    .Select(c => ServiceIdentifier.Normalize(c.Service))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// Mirrors SeedProber's named client. Redirects stay off on purpose: the prober refuses them, so
// a candidate that only works when followed would pass here and fail in production.
var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(Constants.ProbeTimeoutSeconds),
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("TrustScoreAgent-Probe/0.1 (+https://trustscoreagent.com)");
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

var accepted = new List<Candidate>();
var rejected = new List<(Candidate Candidate, string Reason)>();
var warnings = new List<(Candidate Candidate, string Note)>();

foreach (var candidate in candidates)
{
    var normalized = ServiceIdentifier.Normalize(candidate.Service);

    // Best of three. One pass cannot tell a stable endpoint from one that happened to answer,
    // but two is not enough either: rejecting is a hard verdict that gets a target deleted, and a
    // single transient blip must not cause it. This tool rejected a perfectly healthy target on
    // its first run for exactly that reason.
    var passes = new List<ProbeResult>();
    for (var attempt = 0; attempt < Constants.ProbeAttempts; attempt++)
    {
        if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1));
        passes.Add(await ProbeAsync(http, candidate));
    }

    var verdict = Judge(candidate, normalized, passes);
    if (verdict.Reason is not null)
    {
        // This machine is one vantage point, and a bad one is indistinguishable from a bad
        // target. Both of the first run's rejections turned out to be the local network: the
        // registry's own probe, on a different network, measures both services as healthy. So
        // ask the registry before telling anyone to delete a target.
        //
        // Only for failures a vantage point can cause, though. The registry scores the *service*,
        // not this URL, so a healthy score says nothing about a 404 (moved path), a 401/403 (needs
        // a key) or a redirect: those are exactly the broken targets this tool exists to catch,
        // and they would otherwise be waved through whenever the service itself is fine.
        var secondOpinion = FailedOnlyForLocalReasons(passes)
            ? await SecondOpinionAsync(http, normalized)
            : null;
        if (secondOpinion is not null)
        {
            var note = $"failed from here ({verdict.Reason}), but the registry measures it " +
                       $"healthy: score {secondOpinion.Score:0.00} from {secondOpinion.Ratings} " +
                       "ratings. Most likely this network rather than the target. Verify " +
                       "elsewhere before removing it.";
            warnings.Add((candidate, note));
            accepted.Add(candidate);
            Console.WriteLine($"  local?  {candidate.Service}\n            {note}");
            continue;
        }

        rejected.Add((candidate, verdict.Reason));
        Console.WriteLine($"  REJECT  {candidate.Service}\n            {verdict.Reason}");
        continue;
    }

    accepted.Add(candidate);
    foreach (var note in verdict.Notes)
        warnings.Add((candidate, note));

    var noteText = verdict.Notes.Count > 0 ? $"  [{string.Join("; ", verdict.Notes)}]" : "";
    var shown = passes.First(p => p.Error is null && p.StatusCode is >= 200 and < 300);
    Console.WriteLine($"  ok      {candidate.Service}  {shown.StatusCode} in {shown.LatencyMs}ms{noteText}");
}

Console.WriteLine($"\n{accepted.Count} accepted, {rejected.Count} rejected, {warnings.Count} warnings.");

if (warnings.Count > 0)
{
    Console.WriteLine("\nWarnings (accepted, but worth fixing):");
    foreach (var (candidate, note) in warnings)
        Console.WriteLine($"  {candidate.Service}: {note}");
}

if (accepted.Count > 0 && candidatesPath is not null)
{
    Console.WriteLine("\nReady to paste into SeedProbe.Targets:\n");
    Console.WriteLine(JsonSerializer.Serialize(accepted, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));
}

// Non-zero when anything failed, so this can gate a change rather than just inform it.
return rejected.Count == 0 ? 0 : 1;

Verdict Judge(Candidate candidate, string normalized, List<ProbeResult> passes)
{
    var notes = new List<string>();
    var clean = passes.Where(p => p.Error is null && p.StatusCode is >= 200 and < 300).ToList();

    if (clean.Count == 0)
    {
        // Every pass failed the same way, so this is the target and not the weather. Report what
        // the first one actually said: a bare exception type is not enough to act on.
        var first = passes[0];
        if (first.Error is not null)
            return new Verdict($"unreachable on all {passes.Count} passes: {first.Error}");

        // Non-2xx is the trap this tool exists for. An endpoint needing a key answers 401 every
        // time, and the scoring engine counts anything outside 200-299 as a failure, so adding it
        // would publish a permanent outage against a service that is perfectly healthy.
        var hint = first.StatusCode is 401 or 403
            ? " (needs credentials; probe it only through an endpoint that is public)"
            : first.StatusCode is >= 300 and < 400
                ? " (redirects, and the prober does not follow them)"
                : "";
        return new Verdict($"HTTP {first.StatusCode} on all {passes.Count} passes{hint}");
    }

    if (clean.Count < Constants.RequiredCleanPasses)
    {
        var failure = passes.First(p => p.Error is not null || p.StatusCode is < 200 or >= 300);
        return new Verdict(
            $"only {clean.Count} of {passes.Count} passes succeeded " +
            $"(saw {failure.Error ?? $"HTTP {failure.StatusCode}"}). Too unstable to measure a " +
            "service by: its own noise would be published as the service's unreliability.");
    }

    if (clean.Count < passes.Count)
        notes.Add($"flaky: {clean.Count} of {passes.Count} passes succeeded");

    var first_ = clean[0];

    // The probe's own conformity check. If it fails here it fails on every pass in production,
    // quietly holding the service's conformity dimension at zero.
    if (!SeedProber.ValidateBody(first_.Body, candidate.ExpectField, candidate.ExpectText))
    {
        if (!string.IsNullOrEmpty(candidate.ExpectText)
            && !first_.Body.Contains(candidate.ExpectText, StringComparison.Ordinal))
            return new Verdict($"expectText '{candidate.ExpectText}' not found in the response");
        return candidate.ExpectField is null
            ? new Verdict("empty response body")
            : new Verdict($"expectField '{candidate.ExpectField}' not found in the response");
    }

    if (string.IsNullOrWhiteSpace(candidate.ExpectField) && string.IsNullOrEmpty(candidate.ExpectText))
    {
        // Without a field to look for, ValidateBody accepts any non-empty body, so conformity is
        // reported valid no matter what the service returns. The dimension stops measuring
        // anything and silently flatters the score.
        notes.Add("no expectField, so conformity will always report valid");
    }

    if (!normalized.Equals(candidate.Service, StringComparison.OrdinalIgnoreCase))
        notes.Add($"registered as '{normalized}'");

    if (candidatesPath is not null && existing.Contains(normalized))
        notes.Add("already in appsettings");

    // Near the timeout it is one slow day away from being scored as an outage.
    var slowest = clean.Max(p => p.LatencyMs);
    if (slowest > Constants.ProbeTimeoutSeconds * 1000 * 0.5)
        notes.Add($"slow: {slowest}ms against a {Constants.ProbeTimeoutSeconds}s timeout");

    return new Verdict(null, notes);
}

// True when every failed pass failed in a way this machine's network could explain: no response
// at all (DNS, refused, timeout), or a 429 aimed at this address. Any real HTTP answer, or a body
// that fails the conformity check, is the target speaking, and no second opinion overrides it.
static bool FailedOnlyForLocalReasons(List<ProbeResult> passes)
{
    var failed = passes.Where(p => p.Error is not null || p.StatusCode is < 200 or >= 300).ToList();
    return failed.Count > 0 && failed.All(p => p.Error is not null || p.StatusCode == 429);
}

// Asks the public registry what it has measured for a service. Returns null when it has no
// useful history, in which case a local failure is the only evidence available and stands.
static async Task<Reputation?> SecondOpinionAsync(HttpClient http, string service)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var url = $"https://api.trustscoreagent.com/v1/score?service={Uri.EscapeDataString(service)}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, cts.Token));
        var root = doc.RootElement;

        if (!root.GetProperty("known").GetBoolean()) return null;
        var score = root.GetProperty("score").GetDouble();
        var ratings = root.GetProperty("ratings_count").GetInt32();

        // Enough history to outweigh a handful of failures here, and healthy enough that the
        // disagreement is about the network rather than about the service.
        return score >= 0.7 && ratings >= 20 ? new Reputation(score, ratings) : null;
    }
    catch
    {
        // No second opinion available, so the local verdict is all there is.
        return null;
    }
}

static async Task<ProbeResult> ProbeAsync(HttpClient http, Candidate candidate)
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Constants.ProbeTimeoutSeconds));
        using var response = await http.GetAsync(candidate.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        sw.Stop();
        return new ProbeResult((int)response.StatusCode, (int)sw.ElapsedMilliseconds, body, null);
    }
    catch (Exception ex)
    {
        sw.Stop();
        // The type name alone ("HttpRequestException") says nothing actionable, and this tool
        // decides whether a target is allowed in, so the reason has to be legible.
        var detail = ex.InnerException?.Message ?? ex.Message;
        return new ProbeResult(0, (int)sw.ElapsedMilliseconds, "", $"{ex.GetType().Name}: {detail}");
    }
}

static List<Candidate> LoadCandidates(string path) =>
    JsonSerializer.Deserialize<List<Candidate>>(File.ReadAllText(path), Constants.JsonOptions)
    ?? throw new InvalidOperationException($"No candidates in {path}");

static List<Candidate> LoadConfiguredTargets(string appsettingsPath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
    var targets = doc.RootElement.GetProperty("SeedProbe").GetProperty("Targets");
    return targets.EnumerateArray()
        .Select(t => new Candidate(
            t.GetProperty("Service").GetString() ?? "",
            t.GetProperty("Url").GetString() ?? "",
            t.TryGetProperty("ExpectField", out var f) ? f.GetString() : null,
            t.TryGetProperty("ExpectText", out var x) ? x.GetString() : null))
        .ToList();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "migrations")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
}

internal static class Constants
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Matches SeedProbeOptions.TimeoutSeconds, so a candidate is judged against the budget it
    // will actually get.
    public const int ProbeTimeoutSeconds = 10;

    /// <summary>Passes per candidate, and how many must come back clean to accept it.</summary>
    public const int ProbeAttempts = 3;
    public const int RequiredCleanPasses = 2;
}

internal sealed record Candidate(
    [property: JsonPropertyName("Service")] string Service,
    [property: JsonPropertyName("Url")] string Url,
    [property: JsonPropertyName("ExpectField")] string? ExpectField,
    [property: JsonPropertyName("ExpectText")] string? ExpectText = null);

internal sealed record Reputation(double Score, int Ratings);

internal sealed record ProbeResult(int StatusCode, int LatencyMs, string Body, string? Error);

internal sealed record Verdict(string? Reason, List<string>? Notes = null)
{
    public List<string> Notes { get; } = Notes ?? new();
}

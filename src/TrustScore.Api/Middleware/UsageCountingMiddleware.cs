using System.Text.RegularExpressions;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Middleware;

/// <summary>
/// Counts each public API call by endpoint and client family (see <see cref="ClientFamily"/>), so
/// adoption can be measured: how many checks and ratings come from MCP installs or framework
/// integrations rather than from browsers, scripts and crawlers. Health checks are not counted.
/// </summary>
public sealed partial class UsageCountingMiddleware
{
    private readonly RequestDelegate _next;

    public UsageCountingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        var endpoint = EndpointKey(context.Request.Method, context.Request.Path.Value ?? "/");
        if (endpoint is null)
            return;

        // Resolved here, not injected: when Redis is unreachable even building the counter throws,
        // and a lost count must never turn into a failed request.
        try
        {
            var counter = context.RequestServices.GetRequiredService<IUsageCounter>();
            var client = ClientFamily(context.Request.Headers.UserAgent.ToString());
            await counter.RecordAsync(DateOnly.FromDateTime(DateTime.UtcNow), endpoint, client);
        }
        catch (Exception)
        {
            // Best effort by design (see IUsageCounter.RecordAsync).
        }
    }

    /// <summary>
    /// A small, fixed set of endpoint names, so the number of counters stays bounded whatever paths
    /// people request. Returns null for paths that are not worth counting (health, unknown).
    /// </summary>
    public static string? EndpointKey(string method, string path)
    {
        var p = path.TrimEnd('/').ToLowerInvariant();
        return (method.ToUpperInvariant(), p) switch
        {
            ("GET", "/v1/score") => "score",
            ("GET", "/v1/score/history") => "score_history",
            ("GET", "/v1/score/detailed") => "score_detailed",
            ("POST", "/v1/scores/bulk") => "scores_bulk",
            ("POST", "/v1/rate") => "rate",
            ("GET", "/v1/services") => "services",
            ("GET", "/v1/agent/trust") => "agent_trust",
            ("GET", "/v1/audit/root") => "audit_root",
            ("GET", "/v1/audit/anchors") => "audit_anchors",
            ("GET", "/v1/audit/consistency") => "audit_consistency",
            ("GET", "/v1/stats") => "stats",
            ("GET", "/llms.txt") => "llms_txt",
            ("GET", "/.well-known/agent.json") => "agent_card",
            ("GET", _) when p.StartsWith("/v1/audit/proof/") => "audit_proof",
            ("GET", _) when p.StartsWith("/v1/audit/anchors/") && p.EndsWith("/ots") => "audit_ots",
            _ => null,
        };
    }

    /// <summary>
    /// Client family from the User-Agent. Our own clients identify themselves
    /// ("trustscoreagent-mcp/0.2.5", "trustscoreagent-langchain/..."), and only for those is the
    /// version kept; everything else collapses into a handful of families.
    /// </summary>
    public static string ClientFamily(string? userAgent)
    {
        var ua = (userAgent ?? string.Empty).Trim();
        if (ua.Length == 0)
            return "none";

        var own = OwnClient().Match(ua);
        if (own.Success)
            return $"{own.Groups["name"].Value.ToLowerInvariant()}/{own.Groups["version"].Value}";

        var lower = ua.ToLowerInvariant();
        if (lower.StartsWith("trustscoreagent-probe")) return "probe";
        if (lower.Contains("bot") || lower.Contains("crawl") || lower.Contains("spider")) return "bot";
        if (lower.StartsWith("curl/")) return "curl";
        if (lower.StartsWith("python-requests") || lower.StartsWith("python-httpx") || lower.StartsWith("aiohttp")) return "python";
        if (lower == "node" || lower.StartsWith("node-fetch") || lower.StartsWith("undici") || lower.StartsWith("axios")) return "node";
        if (lower.StartsWith("mozilla/")) return "browser";
        return "other";
    }

    [GeneratedRegex(@"^trustscoreagent-(?<name>mcp|langchain|crewai)/(?<version>\d{1,3}\.\d{1,3}\.\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex OwnClient();
}

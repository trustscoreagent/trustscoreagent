using FluentAssertions;
using TrustScore.Api.Middleware;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// The classification behind /v1/stats: a fixed set of endpoint names (so counters stay bounded
/// whatever paths are requested) and a handful of client families, with a version kept only for
/// the registry's own clients.
/// </summary>
public class UsageCountingTests
{
    [Theory]
    [InlineData("GET", "/v1/score", "score")]
    [InlineData("get", "/v1/score/", "score")]
    [InlineData("GET", "/V1/Score/History", "score_history")]
    [InlineData("POST", "/v1/rate", "rate")]
    [InlineData("POST", "/v1/scores/bulk", "scores_bulk")]
    [InlineData("GET", "/v1/audit/proof/7f0c2b9e-0000-0000-0000-000000000000", "audit_proof")]
    [InlineData("GET", "/.well-known/agent.json", "agent_card")]
    [InlineData("GET", "/v1/stats", "stats")]
    public void EndpointKey_NamesThePublicEndpoints(string method, string path, string expected)
        => UsageCountingMiddleware.EndpointKey(method, path).Should().Be(expected);

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/wp-login.php")]
    [InlineData("GET", "/v1/rate")]
    [InlineData("POST", "/v1/score")]
    [InlineData("POST", "/v1/admin/eigentrust")]
    public void EndpointKey_IgnoresEverythingElse(string method, string path)
        => UsageCountingMiddleware.EndpointKey(method, path).Should().BeNull();

    [Theory]
    [InlineData("trustscoreagent-mcp/0.2.5", "mcp/0.2.5")]
    [InlineData("TrustScoreAgent-LangChain/1.0.0 python-requests/2.32", "langchain/1.0.0")]
    [InlineData("trustscoreagent-crewai/0.1.0", "crewai/0.1.0")]
    [InlineData("trustscoreagent-probe/1.0 (+https://trustscoreagent.com)", "probe")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1)", "bot")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130", "browser")]
    [InlineData("curl/8.9.1", "curl")]
    [InlineData("python-requests/2.32.3", "python")]
    [InlineData("node", "node")]
    [InlineData("undici", "node")]
    [InlineData("SomethingElse/1.0", "other")]
    [InlineData("", "none")]
    [InlineData(null, "none")]
    public void ClientFamily_GroupsUserAgents(string? userAgent, string expected)
        => UsageCountingMiddleware.ClientFamily(userAgent).Should().Be(expected);

    [Fact]
    public void ClientFamily_NeverKeepsAFreeFormVersion()
    {
        // Anyone can claim to be our client; an unbounded "version" would grow the counters.
        UsageCountingMiddleware.ClientFamily("trustscoreagent-mcp/" + new string('9', 500)).Should().Be("other");
        UsageCountingMiddleware.ClientFamily("trustscoreagent-mcp/latest").Should().Be("other");
    }
}

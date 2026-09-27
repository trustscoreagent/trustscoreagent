using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// One release, one number. The MCP listings once sat at 0.1.1 while npm was at 0.2.2, and the API
/// reported 0.1.0 for months, because each file was bumped by hand. This fails CI instead.
/// </summary>
public class VersionConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TrustScore.sln"))
               && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Json(string relative, params string[] path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), relative)));
        var e = doc.RootElement;
        foreach (var p in path)
            e = int.TryParse(p, out var i) ? e[i] : e.GetProperty(p);
        return e.GetString()!;
    }

    [Fact]
    public void ApiAgentCardAndMcpListings_ShareOneVersion()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", "TrustScore.Api", "TrustScore.Api.csproj"))
            .Descendants("Version").Single().Value;

        var versions = new Dictionary<string, string>
        {
            ["TrustScore.Api.csproj"] = csproj,
            ["public/.well-known/agent.json"] = Json("public/.well-known/agent.json", "version"),
            ["mcp-server/package.json"] = Json("mcp-server/package.json", "version"),
            ["mcp-server/server.json"] = Json("mcp-server/server.json", "version"),
            ["mcp-server/server.json packages[0]"] = Json("mcp-server/server.json", "packages", "0", "version"),
            ["mcp-server/manifest.json"] = Json("mcp-server/manifest.json", "version"),
        };

        TrustScore.Api.Endpoints.HealthEndpoints.Version.Should().Be(csproj, "/health reports the release version");

        versions.Values.Distinct().Should().ContainSingle(
            $"every file must carry the release version, found: {string.Join(", ", versions.Select(v => $"{v.Key}={v.Value}"))}");
    }
}

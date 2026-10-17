using FluentAssertions;
using TrustScore.Api.Data;
using TrustScore.Core.Interfaces;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>The x402 probe's selection table, against a real PostgreSQL.</summary>
public class X402TargetRepositoryTests : PostgresDatabaseTest
{
    [PostgresFact]
    public async Task ReplaceSelection_ActivatesTheNewOnes_DeactivatesTheRest_AndRecordsWhen()
    {
        var repo = new X402TargetRepository(Db);
        (await repo.GetLastRefreshAsync()).Should().BeNull();

        var first = DateTimeOffset.UtcNow.AddDays(-1);
        await repo.ReplaceSelectionAsync(
            [
                new X402ProbeTarget("a.test/x", "https://a.test/x", "GET", 50),
                new X402ProbeTarget("b.test/y", "https://b.test/y?k=1", "POST", 20),
            ], first);

        (await repo.GetActiveAsync()).Select(t => t.ServiceDid).Should().Equal("a.test/x", "b.test/y");

        var second = DateTimeOffset.UtcNow;
        await repo.ReplaceSelectionAsync(
            [
                new X402ProbeTarget("b.test/y", "https://b.test/y?k=2", "POST", 30),
                new X402ProbeTarget("c.test/z", "https://c.test/z", "HEAD", 15),
            ], second);

        var active = await repo.GetActiveAsync();
        active.Should().Equal(
            new X402ProbeTarget("b.test/y", "https://b.test/y?k=2", "POST", 30),
            new X402ProbeTarget("c.test/z", "https://c.test/z", "HEAD", 15));
        (await repo.GetLastRefreshAsync()).Should().BeCloseTo(second, TimeSpan.FromMilliseconds(5));

        // A.test is kept, inactive, and comes back when selected again.
        await repo.ReplaceSelectionAsync([new X402ProbeTarget("a.test/x", "https://a.test/x", "GET", 60)], second);
        (await repo.GetActiveAsync()).Should().ContainSingle().Which.UniquePayers.Should().Be(60);
    }
}

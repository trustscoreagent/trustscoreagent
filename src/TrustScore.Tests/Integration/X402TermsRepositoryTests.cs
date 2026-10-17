using Dapper;
using FluentAssertions;
using TrustScore.Api.Data;
using TrustScore.Core.X402;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>The x402 payment terms and their changes, against a real PostgreSQL.</summary>
public class X402TermsRepositoryTests : PostgresDatabaseTest
{
    private static X402Challenge Challenge(string amount, string payTo) =>
        new(2, [new X402Requirement("exact", "eip155:8453", amount, "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", payTo)]);

    [PostgresFact]
    public async Task Record_StoresTheFirstTerms_ThenDatesAndCountsEachChange()
    {
        var repo = new X402TermsRepository(Db);
        (await repo.GetAsync("paid.test/x")).Should().BeNull();

        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        (await repo.RecordAsync("paid.test/x", Challenge("10000", "0xAAAA"), t0)).Any.Should().BeFalse();

        var first = (await repo.GetAsync("paid.test/x"))!;
        first.Version.Should().Be(2);
        first.Accepts.Should().ContainSingle().Which.PayTo.Should().Be("0xAAAA");
        first.FirstSeenAt.Should().BeCloseTo(t0, TimeSpan.FromMilliseconds(5));
        first.PayToChangedAt.Should().BeNull();
        first.PayToChanges.Should().Be(0);

        // Same terms, the address in another case: nothing changed but the last sighting.
        var t1 = t0.AddHours(6);
        (await repo.RecordAsync("paid.test/x", Challenge("10000", "0xaaaa"), t1)).Any.Should().BeFalse();

        var t2 = t1.AddHours(6);
        (await repo.RecordAsync("paid.test/x", Challenge("10000", "0xBBBB"), t2))
            .Should().Be(new X402TermsChange(PayTo: true, Price: false));

        var t3 = t2.AddHours(6);
        (await repo.RecordAsync("paid.test/x", Challenge("20000", "0xBBBB"), t3))
            .Should().Be(new X402TermsChange(PayTo: false, Price: true));

        var now = (await repo.GetAsync("paid.test/x"))!;
        now.Accepts.Should().ContainSingle().Which.Should().Be(
            new X402Requirement("exact", "eip155:8453", "20000", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", "0xBBBB"));
        now.FirstSeenAt.Should().BeCloseTo(t0, TimeSpan.FromMilliseconds(5));
        now.LastSeenAt.Should().BeCloseTo(t3, TimeSpan.FromMilliseconds(5));
        now.PayToChangedAt.Should().BeCloseTo(t2, TimeSpan.FromMilliseconds(5));
        now.PayToChanges.Should().Be(1);
        now.PriceChangedAt.Should().BeCloseTo(t3, TimeSpan.FromMilliseconds(5));
        now.PriceChanges.Should().Be(1);
        now.Observations.Should().Be(4);
        now.PayToStreak.Should().Be(0, "the last observation kept the payee");
        now.PriceStreak.Should().Be(1);

        using var conn = Db.CreateConnection();
        var history = (await conn.QueryAsync<(bool PayTo, bool Price, string Previous)>(
            """
            SELECT pay_to_changed, price_changed, previous_accepts::text
            FROM x402_terms_changes WHERE service_did = 'paid.test/x' ORDER BY observed_at
            """)).ToList();
        history.Select(h => (h.PayTo, h.Price)).Should().Equal((true, false), (false, true));
        history[0].Previous.Should().Contain("0xaaaa", "the change keeps what it replaced");
    }

    [PostgresFact]
    public async Task APayeeNewOnEveryObservation_ReadsAsRotating_AndStopsWhenItHolds()
    {
        var repo = new X402TermsRepository(Db);
        var t = DateTimeOffset.UtcNow.AddDays(-1);

        await repo.RecordAsync("paid.test/r", Challenge("10000", "0x01"), t);
        await repo.RecordAsync("paid.test/r", Challenge("10000", "0x02"), t = t.AddHours(6));
        await repo.RecordAsync("paid.test/r", Challenge("10000", "0x03"), t = t.AddHours(6));
        (await repo.GetAsync("paid.test/r"))!.PayToRotates.Should().BeFalse("two changes may be a swap and its fix");

        await repo.RecordAsync("paid.test/r", Challenge("10000", "0x04"), t = t.AddHours(6));
        var rotating = (await repo.GetAsync("paid.test/r"))!;
        rotating.PayToRotates.Should().BeTrue();
        rotating.PriceVaries.Should().BeFalse();

        await repo.RecordAsync("paid.test/r", Challenge("10000", "0x04"), t.AddHours(6));
        (await repo.GetAsync("paid.test/r"))!.PayToRotates.Should().BeFalse();
    }
}

using System.Text.Json;
using Dapper;
using TrustScore.Core.Interfaces;
using TrustScore.Core.X402;

namespace TrustScore.Api.Data;

public sealed class X402TermsRepository : IX402TermsRepository
{
    private readonly DbConnectionFactory _db;

    public X402TermsRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<X402TermsRecord?> GetAsync(string serviceDid)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT service_did AS ServiceDid, x402_version AS Version, accepts::text AS Accepts,
                   first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt,
                   pay_to_changed_at AS PayToChangedAt, pay_to_changes AS PayToChanges,
                   price_changed_at AS PriceChangedAt, price_changes AS PriceChanges,
                   observations AS Observations, pay_to_streak AS PayToStreak, price_streak AS PriceStreak
            FROM x402_terms
            WHERE service_did = @serviceDid
            """, new { serviceDid });
        return row is null
            ? null
            : new X402TermsRecord(
                row.ServiceDid, row.Version, FromJson(row.Accepts),
                Utc(row.FirstSeenAt), Utc(row.LastSeenAt),
                row.PayToChangedAt is { } p ? Utc(p) : null, row.PayToChanges,
                row.PriceChangedAt is { } c ? Utc(c) : null, row.PriceChanges,
                row.Observations, row.PayToStreak, row.PriceStreak);
    }

    public async Task<X402TermsChange> RecordAsync(string serviceDid, X402Challenge challenge, DateTimeOffset now)
    {
        var accepts = X402Terms.Canonical(challenge.Accepts);
        var json = ToJson(accepts);

        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var previous = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT accepts::text FROM x402_terms WHERE service_did = @serviceDid FOR UPDATE",
            new { serviceDid }, transaction: tx);

        if (previous is null)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO x402_terms (service_did, x402_version, accepts, first_seen_at, last_seen_at)
                VALUES (@serviceDid, @Version, @json::jsonb, @now, @now)
                ON CONFLICT (service_did) DO NOTHING
                """,
                new { serviceDid, challenge.Version, json, now }, transaction: tx);
            tx.Commit();
            return default;
        }

        var change = X402Terms.Compare(FromJson(previous), accepts);
        await conn.ExecuteAsync(
            """
            UPDATE x402_terms SET
                x402_version      = @Version,
                accepts           = @json::jsonb,
                last_seen_at      = @now,
                observations      = observations + 1,
                pay_to_streak     = CASE WHEN @PayTo THEN pay_to_streak + 1 ELSE 0 END,
                price_streak      = CASE WHEN @Price THEN price_streak + 1 ELSE 0 END,
                pay_to_changed_at = CASE WHEN @PayTo THEN @now ELSE pay_to_changed_at END,
                pay_to_changes    = pay_to_changes + CASE WHEN @PayTo THEN 1 ELSE 0 END,
                price_changed_at  = CASE WHEN @Price THEN @now ELSE price_changed_at END,
                price_changes     = price_changes + CASE WHEN @Price THEN 1 ELSE 0 END
            WHERE service_did = @serviceDid
            """,
            new { serviceDid, challenge.Version, json, now, change.PayTo, change.Price }, transaction: tx);

        if (change.Any)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO x402_terms_changes
                    (service_did, observed_at, pay_to_changed, price_changed, previous_accepts, accepts)
                VALUES (@serviceDid, @now, @PayTo, @Price, @previous::jsonb, @json::jsonb)
                """,
                new { serviceDid, now, change.PayTo, change.Price, previous, json }, transaction: tx);
        }

        tx.Commit();
        return change;
    }

    // Stored in snake_case, like the API returns it.
    private static string ToJson(IReadOnlyList<X402Requirement> accepts) =>
        JsonSerializer.Serialize(accepts.Select(a => new StoredRequirement(a.Scheme, a.Network, a.Asset, a.Amount, a.PayTo)));

    private static IReadOnlyList<X402Requirement> FromJson(string json) =>
        (JsonSerializer.Deserialize<List<StoredRequirement>>(json) ?? [])
            .Select(s => new X402Requirement(s.Scheme, s.Network, s.Amount, s.Asset, s.PayTo))
            .ToList();

    // Npgsql reads timestamptz as a UTC DateTime.
    private static DateTimeOffset Utc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    private sealed record StoredRequirement(
        [property: System.Text.Json.Serialization.JsonPropertyName("scheme")] string Scheme,
        [property: System.Text.Json.Serialization.JsonPropertyName("network")] string Network,
        [property: System.Text.Json.Serialization.JsonPropertyName("asset")] string Asset,
        [property: System.Text.Json.Serialization.JsonPropertyName("amount")] string Amount,
        [property: System.Text.Json.Serialization.JsonPropertyName("pay_to")] string PayTo);

    private sealed class Row
    {
        public string ServiceDid { get; init; } = "";
        public int Version { get; init; }
        public string Accepts { get; init; } = "[]";
        public DateTime FirstSeenAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public DateTime? PayToChangedAt { get; init; }
        public int PayToChanges { get; init; }
        public DateTime? PriceChangedAt { get; init; }
        public int PriceChanges { get; init; }
        public int Observations { get; init; }
        public int PayToStreak { get; init; }
        public int PriceStreak { get; init; }
    }
}

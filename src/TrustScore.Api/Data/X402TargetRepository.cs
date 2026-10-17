using Dapper;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

public sealed class X402TargetRepository : IX402TargetRepository
{
    private readonly DbConnectionFactory _db;

    public X402TargetRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<X402ProbeTarget>> GetActiveAsync()
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<X402ProbeTarget>(
            """
            SELECT service_did AS ServiceDid, url AS Url, method AS Method, unique_payers AS UniquePayers
            FROM x402_probe_targets
            WHERE active
            ORDER BY unique_payers DESC, service_did
            """);
        return rows.ToList();
    }

    public async Task<DateTimeOffset?> GetLastRefreshAsync()
    {
        using var conn = _db.CreateConnection();
        // Npgsql reads timestamptz as a UTC DateTime.
        var last = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(refreshed_at) FROM x402_probe_targets");
        return last is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
    }

    public async Task ReplaceSelectionAsync(IReadOnlyList<X402ProbeTarget> targets, DateTimeOffset now)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "UPDATE x402_probe_targets SET active = FALSE WHERE active", transaction: tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO x402_probe_targets
                (service_did, url, method, unique_payers, active, selected_at, refreshed_at)
            VALUES (@ServiceDid, @Url, @Method, @UniquePayers, TRUE, @Now, @Now)
            ON CONFLICT (service_did) DO UPDATE SET
                url           = EXCLUDED.url,
                method        = EXCLUDED.method,
                unique_payers = EXCLUDED.unique_payers,
                active        = TRUE,
                selected_at   = EXCLUDED.selected_at,
                refreshed_at  = EXCLUDED.refreshed_at
            """,
            targets.Select(t => new { t.ServiceDid, t.Url, t.Method, t.UniquePayers, Now = now }),
            transaction: tx);

        // Rows that left the selection keep their history but say when the last selection ran,
        // so GetLastRefreshAsync is right even when nothing was selected.
        await conn.ExecuteAsync(
            "UPDATE x402_probe_targets SET refreshed_at = @Now WHERE NOT active",
            new { Now = now }, transaction: tx);

        tx.Commit();
    }
}

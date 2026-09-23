using Dapper;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Data;

public sealed class ProbeHealthRepository : IProbeHealthRepository
{
    private readonly DbConnectionFactory _db;

    public ProbeHealthRepository(DbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<string, ProbeTargetHealth>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<ProbeTargetHealth>(
            """
            SELECT service_did          AS ServiceDid,
                   consecutive_failures AS ConsecutiveFailures,
                   quarantined_at       AS QuarantinedAt,
                   last_status_code     AS LastStatusCode,
                   last_probed_at       AS LastProbedAt,
                   failing_since        AS FailingSince
            FROM probe_target_health
            """);

        return rows.ToDictionary(r => r.ServiceDid);
    }

    public async Task UpsertAsync(ProbeTargetHealth health)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO probe_target_health
                (service_did, consecutive_failures, quarantined_at, last_status_code, last_probed_at, failing_since)
            VALUES (@ServiceDid, @ConsecutiveFailures, @QuarantinedAt, @LastStatusCode, @LastProbedAt, @FailingSince)
            ON CONFLICT (service_did) DO UPDATE SET
                consecutive_failures = EXCLUDED.consecutive_failures,
                quarantined_at       = EXCLUDED.quarantined_at,
                last_status_code     = EXCLUDED.last_status_code,
                last_probed_at       = EXCLUDED.last_probed_at,
                failing_since        = EXCLUDED.failing_since
            """,
            health);
    }
}

using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

/// <summary>
/// Persists how each seed-probe target has been behaving, so a probe URL that has rotted can be
/// told apart from a service that is genuinely down. The state has to outlive the process: the
/// probe runs as a Cloud Run Job that exits after every pass.
/// </summary>
public interface IProbeHealthRepository
{
    /// <summary>Health for every target that has been probed at least once, keyed by service.</summary>
    Task<IReadOnlyDictionary<string, ProbeTargetHealth>> GetAllAsync();

    Task UpsertAsync(ProbeTargetHealth health);
}

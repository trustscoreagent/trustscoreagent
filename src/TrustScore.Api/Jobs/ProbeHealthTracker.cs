using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Jobs;

/// <summary>
/// The quarantine bookkeeping shared by the probes: updates a target's health after a
/// measurement and says whether that measurement should become a rating. A target failing for
/// days is far likelier to be a stale URL of ours than an outage, and publishing it would accuse a
/// third party on the strength of our own configuration.
/// </summary>
internal sealed class ProbeHealthTracker
{
    private readonly IProbeHealthRepository _repo;
    private readonly IReadOnlyDictionary<string, ProbeTargetHealth>? _health;
    private readonly int _minConsecutiveFailures;
    private readonly TimeSpan _quarantineAfter;
    private readonly ILogger _logger;
    private readonly string _probe;

    private ProbeHealthTracker(
        IProbeHealthRepository repo,
        IReadOnlyDictionary<string, ProbeTargetHealth>? health,
        int minConsecutiveFailures,
        TimeSpan quarantineAfter,
        ILogger logger,
        string probe)
    {
        _repo = repo;
        _health = health;
        _minConsecutiveFailures = minConsecutiveFailures;
        _quarantineAfter = quarantineAfter;
        _logger = logger;
        _probe = probe;
    }

    /// <summary>
    /// Reads every target's health once for the pass. Health is bookkeeping around the
    /// measurement, so failing to read it must not cancel the pass. Without it a quarantined
    /// target cannot be told from a healthy one, so the pass publishes only successes (which
    /// would lift a quarantine anyway) and holds back failures, which are exactly what
    /// quarantine exists to suppress.
    /// </summary>
    public static async Task<ProbeHealthTracker> LoadAsync(
        IProbeHealthRepository repo, int minConsecutiveFailures, TimeSpan quarantineAfter, ILogger logger, string probe)
    {
        IReadOnlyDictionary<string, ProbeTargetHealth>? health;
        try
        {
            health = await repo.GetAllAsync();
        }
        catch (Exception ex)
        {
            health = null;
            logger.LogWarning(ex,
                "{Probe}: target health unavailable; publishing successes only and holding back failures this pass",
                probe);
        }
        return new ProbeHealthTracker(repo, health, minConsecutiveFailures, quarantineAfter, logger, probe);
    }

    /// <summary>Records the outcome and returns whether to publish it as a rating.</summary>
    public async Task<bool> RecordAsync(string serviceId, bool usable, int statusCode, string url, DateTimeOffset now)
    {
        if (_health is null)
            // Health is not written back either: starting from an assumed-clean state would wipe
            // a real failure streak.
            return usable;

        var before = _health.GetValueOrDefault(serviceId) ?? ProbeTargetHealth.Unseen(serviceId);
        var after = usable
            ? before.AfterSuccess(statusCode, now)
            : before.AfterFailure(statusCode, now, _minConsecutiveFailures, _quarantineAfter);

        await TrySaveAsync(after);
        return ShouldPublish(before, after, statusCode, url);
    }

    /// <summary>
    /// Whether this result should become a rating, logging quarantine transitions. A failed
    /// health write has no say here: the decision is made from the state computed in memory, so a
    /// hiccup in the bookkeeping never costs a real measurement.
    /// </summary>
    private bool ShouldPublish(ProbeTargetHealth before, ProbeTargetHealth after, int statusCode, string url)
    {
        if (after.IsQuarantined)
        {
            if (!before.IsQuarantined)
            {
                _logger.LogWarning(
                    "{Probe} {Service}: quarantined after failing since {FailingSince} ({Failures} consecutive " +
                    "failures, last HTTP {Status}). The probe URL has most likely moved. Ratings are " +
                    "suppressed for this target until it answers again: {Url}",
                    _probe, after.ServiceDid, after.FailingSince, after.ConsecutiveFailures, statusCode, url);
            }
            return false;
        }

        if (before.IsQuarantined)
        {
            _logger.LogInformation(
                "{Probe} {Service}: answering again, leaving quarantine", _probe, after.ServiceDid);
        }
        return true;
    }

    private async Task TrySaveAsync(ProbeTargetHealth health)
    {
        try
        {
            await _repo.UpsertAsync(health);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Probe} {Service}: could not save target health", _probe, health.ServiceDid);
        }
    }
}

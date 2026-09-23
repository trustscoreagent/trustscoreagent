namespace TrustScore.Core.Models;

/// <summary>
/// How one seed-probe target has been answering. Exists to separate "this service is down" from
/// "our probe URL is wrong", which look identical in a single response and only differ in how
/// long they last.
///
/// <see cref="FailingSince"/> is when the current unbroken run of failures began. Duration is the
/// signal, so the quarantine rule reads it directly instead of inferring it from a pass count,
/// which silently changes meaning whenever the probe schedule does.
/// </summary>
public sealed record ProbeTargetHealth(
    string ServiceDid,
    int ConsecutiveFailures,
    DateTimeOffset? QuarantinedAt,
    int? LastStatusCode,
    DateTimeOffset LastProbedAt,
    DateTimeOffset? FailingSince)
{
    /// <summary>
    /// Quarantined targets are still probed on every pass, so a target that starts answering
    /// again recovers by itself, but their results are not recorded as ratings.
    /// </summary>
    public bool IsQuarantined => QuarantinedAt is not null;

    public static ProbeTargetHealth Unseen(string serviceDid) =>
        new(serviceDid, 0, null, null, DateTimeOffset.MinValue, null);

    /// <summary>
    /// Health after a probe that returned a usable response. Clears any quarantine: answering is
    /// the only evidence that the URL is right, whatever it did before.
    /// </summary>
    public ProbeTargetHealth AfterSuccess(int statusCode, DateTimeOffset now) =>
        this with
        {
            ConsecutiveFailures = 0,
            QuarantinedAt = null,
            LastStatusCode = statusCode,
            LastProbedAt = now,
            FailingSince = null,
        };

    /// <summary>
    /// Health after a failed probe. The target is quarantined once it has been failing for at
    /// least <paramref name="quarantineAfter"/> AND has failed at least
    /// <paramref name="minConsecutiveFailures"/> probes in a row. Both are needed: the duration is
    /// the actual evidence, and the count stops two failures that happen to be days apart (a
    /// paused scheduler, say) from looking like days of failing.
    ///
    /// Quarantine latches on and is not re-stamped afterwards, so <see cref="QuarantinedAt"/>
    /// keeps saying when trust in the target was lost.
    /// </summary>
    public ProbeTargetHealth AfterFailure(
        int statusCode, DateTimeOffset now, int minConsecutiveFailures, TimeSpan quarantineAfter)
    {
        var failures = ConsecutiveFailures + 1;
        var failingSince = FailingSince ?? now;
        var quarantine = failures >= minConsecutiveFailures && now - failingSince >= quarantineAfter;
        return this with
        {
            ConsecutiveFailures = failures,
            QuarantinedAt = QuarantinedAt ?? (quarantine ? now : null),
            LastStatusCode = statusCode,
            LastProbedAt = now,
            FailingSince = failingSince,
        };
    }
}

namespace TrustScore.Core.Models;

/// <summary>
/// How one seed-probe target has been answering. Exists to separate "this service is down" from
/// "our probe URL is wrong", which look identical in a single response and only differ in how
/// long they last.
/// </summary>
public sealed record ProbeTargetHealth(
    string ServiceDid,
    int ConsecutiveFailures,
    DateTimeOffset? QuarantinedAt,
    int? LastStatusCode,
    DateTimeOffset LastProbedAt)
{
    /// <summary>
    /// Quarantined targets are still probed on every pass, so a target that starts answering
    /// again recovers by itself, but their results are not recorded as ratings.
    /// </summary>
    public bool IsQuarantined => QuarantinedAt is not null;

    public static ProbeTargetHealth Unseen(string serviceDid) =>
        new(serviceDid, 0, null, null, DateTimeOffset.MinValue);

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
        };

    /// <summary>
    /// Health after a failed probe. Quarantine latches on at the threshold and is not re-stamped
    /// afterwards, so <see cref="QuarantinedAt"/> keeps saying when trust in the target was lost.
    /// </summary>
    public ProbeTargetHealth AfterFailure(int statusCode, DateTimeOffset now, int quarantineThreshold)
    {
        var failures = ConsecutiveFailures + 1;
        return this with
        {
            ConsecutiveFailures = failures,
            QuarantinedAt = QuarantinedAt ?? (failures >= quarantineThreshold ? now : null),
            LastStatusCode = statusCode,
            LastProbedAt = now,
        };
    }
}

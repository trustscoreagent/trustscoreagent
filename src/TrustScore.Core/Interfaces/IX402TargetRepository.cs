namespace TrustScore.Core.Interfaces;

/// <summary>An x402 endpoint the batch job probes, as picked from the Bazaar catalog.</summary>
public sealed record X402ProbeTarget(string ServiceDid, string Url, string Method, int UniquePayers);

public interface IX402TargetRepository
{
    /// <summary>The endpoints of the current selection.</summary>
    Task<IReadOnlyList<X402ProbeTarget>> GetActiveAsync();

    /// <summary>When the current selection was made, or null before the first one.</summary>
    Task<DateTimeOffset?> GetLastRefreshAsync();

    /// <summary>
    /// Makes <paramref name="targets"/> the selection: they are inserted or updated and active,
    /// every other endpoint becomes inactive. Atomic, so a probe pass never sees half of it.
    /// </summary>
    Task ReplaceSelectionAsync(IReadOnlyList<X402ProbeTarget> targets, DateTimeOffset now);
}

using TrustScore.Core.X402;

namespace TrustScore.Core.Interfaces;

/// <summary>The x402 payment terms the probe saw on each endpoint, and their changes.</summary>
public interface IX402TermsRepository
{
    /// <summary>The terms last seen on <paramref name="serviceDid"/>, or null when none was.</summary>
    Task<X402TermsRecord?> GetAsync(string serviceDid);

    /// <summary>
    /// Records a valid challenge seen on <paramref name="serviceDid"/> at <paramref name="now"/>:
    /// the first one is stored as is; a later one that differs updates the stored terms, the
    /// change dates and counts, and adds the change (before and after) to the history. Returns
    /// what changed.
    /// </summary>
    Task<X402TermsChange> RecordAsync(string serviceDid, X402Challenge challenge, DateTimeOffset now);
}

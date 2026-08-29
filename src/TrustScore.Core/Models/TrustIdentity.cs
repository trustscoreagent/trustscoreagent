namespace TrustScore.Core.Models;

/// <summary>
/// The identity a rating counts under when agent reputation is computed.
///
/// A rating's <c>agent_did</c> means two very different things depending on how it arrived. If the
/// request was signed, the sender proved it holds the key behind that DID. If it was not, the DID
/// is a header anyone can set, so treating both as the same rater would let an attacker submit
/// deliberately inconsistent ratings in a victim's name and drive that victim's EigenTrust score
/// down, which in turn shrinks the weight of the victim's own honest ratings.
///
/// Unsigned ratings therefore accumulate under a separate, namespaced identity. They still feed
/// service consensus (dropping them would discard almost all of the data), they just cannot spend
/// or damage the reputation of an identity they merely claim.
/// </summary>
public static class TrustIdentity
{
    /// <summary>
    /// Prefix for raters whose DID was asserted rather than proven. Chosen to be impossible to
    /// forge from the outside: a DID must contain a colon after its method, and no DID method is
    /// named "unsigned", so a caller cannot craft an <c>X-Agent-DID</c> that collides with the
    /// namespaced form of somebody else's identity.
    /// </summary>
    public const string UnsignedPrefix = "unsigned:";

    /// <summary>
    /// The identity <paramref name="agentDid"/> accrues reputation under, given whether the rating
    /// was actually attributable to it.
    /// </summary>
    public static string For(string agentDid, bool signatureVerified) =>
        signatureVerified ? agentDid : UnsignedPrefix + agentDid;
}

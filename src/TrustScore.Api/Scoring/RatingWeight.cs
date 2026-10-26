namespace TrustScore.Api.Scoring;

/// <summary>
/// The weight a submitted rating counts at. One function, so the endpoint and the simulations
/// that test its economics cannot drift apart.
/// </summary>
public static class RatingWeight
{
    /// <summary>Base weight of a rating without a receipt that vouches for it.</summary>
    public const double Unverified = 0.3;

    /// <summary>Base weight of a rating backed by a verified receipt for the call it reports.</summary>
    public const double Verified = 1.0;

    /// <summary>
    /// How much an unsigned rating is discounted. Identity and attestation are orthogonal: a
    /// receipt says the service saw the call, a signature says we know who is reporting it. An
    /// unsigned rating still counts (existing clients keep working) but only half, because its
    /// X-Agent-DID is self-asserted and could name any agent.
    /// </summary>
    public const double UnsignedIdentityFactor = 0.5;

    /// <param name="receiptVerified">A verified receipt covers the call this rating reports.</param>
    /// <param name="signed">The request carried a valid agent signature.</param>
    /// <param name="trust">Trust of the identity the rating accrues under.</param>
    /// <param name="repeat">This is the n-th rating of the provider from the same source in the
    /// repeat window; it counts 1/n.</param>
    /// <param name="allowanceExhausted">The rater has no earned trust and the newcomers' daily
    /// allowance for this provider is used up.</param>
    public static double Counted(bool receiptVerified, bool signed, double trust, int repeat, bool allowanceExhausted)
    {
        if (allowanceExhausted)
            return 0.0;

        // A receipt is signed by the service the rating is about, so it lends full weight only to
        // an agent that has earned trust: otherwise a service could vouch for itself through any
        // number of fresh keys.
        var baseWeight = receiptVerified && trust > EigenTrustEngine.DefaultTrustScore ? Verified : Unverified;
        var weight = baseWeight * (signed ? 1.0 : UnsignedIdentityFactor) * trust;
        return weight / Math.Max(1, repeat);
    }
}

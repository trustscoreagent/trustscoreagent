using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IReceiptVerifier
{
    /// <param name="expectedAgentDid">
    /// The DID of the agent submitting the rating. The receipt's signed agent_did must match it,
    /// otherwise the receipt is not attesting THIS submitter (a stolen/replayed receipt) and is
    /// rejected before its nonce is claimed.
    /// </param>
    Task<ReceiptVerificationResult> VerifyAsync(string jwt, string expectedServiceDid, string expectedAgentDid);
}

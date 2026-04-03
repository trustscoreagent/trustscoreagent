using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IReceiptVerifier
{
    Task<ReceiptVerificationResult> VerifyAsync(string jwt, string expectedServiceDid);
}

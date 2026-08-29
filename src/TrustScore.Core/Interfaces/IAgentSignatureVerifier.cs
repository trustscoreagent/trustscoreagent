using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IAgentSignatureVerifier
{
    /// <summary>
    /// Verifies that the caller holds the private key behind <c>X-Agent-DID</c>, binding the
    /// signature to this exact request (method, path, timestamp, nonce, body bytes).
    /// </summary>
    /// <param name="body">
    /// The raw request body as received. It is bound by hash, so the caller must pass the exact
    /// bytes that were read off the wire, not a re-serialisation of the parsed model.
    /// </param>
    Task<AgentSignatureResult> VerifyAsync(
        AgentSignatureHeaders headers,
        string httpMethod,
        string requestPath,
        byte[] body);
}

using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IAgentSignatureVerifier
{
    /// <summary>
    /// Verifies that the caller holds the private key behind <c>X-Agent-DID</c>, binding the
    /// signature to this exact request (method, path, timestamp, nonce, body bytes).
    /// </summary>
    /// <param name="requestHost">
    /// The host this request arrived on, used as the audience when none is configured. Note that a
    /// deployment which does not configure a canonical audience gets no cross-registry protection,
    /// because this value comes from the (attacker-controlled) Host header.
    /// </param>
    /// <param name="body">
    /// The raw request body as received. It is bound by hash, so the caller must pass the exact
    /// bytes that were read off the wire, not a re-serialisation of the parsed model.
    /// </param>
    Task<AgentSignatureResult> VerifyAsync(
        AgentSignatureHeaders headers,
        string requestHost,
        string httpMethod,
        string requestPath,
        byte[] body);
}

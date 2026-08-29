using System.Security.Cryptography;
using System.Text;

namespace TrustScore.Core.Models;

/// <summary>
/// The signature headers an agent sends to prove it holds the key behind its DID.
/// All four are absent on an unsigned (legacy) request, which is not an error.
/// </summary>
public sealed record AgentSignatureHeaders(
    string? AgentDid,
    string? Signature,
    string? Timestamp,
    string? Nonce)
{
    public const string DidHeader = "X-Agent-DID";
    public const string SignatureHeader = "X-Agent-Signature";
    public const string TimestampHeader = "X-Agent-Timestamp";
    public const string NonceHeader = "X-Agent-Nonce";

    /// <summary>
    /// True when the caller attempted to sign at all. A request with none of the signature headers
    /// is an unsigned legacy request (accepted at reduced weight); a request with some of them is a
    /// malformed signed request and must be rejected rather than silently downgraded.
    /// </summary>
    public bool IsSignatureAttempted =>
        !string.IsNullOrWhiteSpace(Signature)
        || !string.IsNullOrWhiteSpace(Timestamp)
        || !string.IsNullOrWhiteSpace(Nonce);
}

public enum AgentSignatureStatus
{
    /// <summary>Signature present, verified, and its nonce claimed.</summary>
    Valid,

    /// <summary>No signature headers at all: an unsigned legacy request, not a failure.</summary>
    Missing,

    /// <summary>Headers present but unusable (absent field, out of bounds, bad encoding).</summary>
    Malformed,

    /// <summary>The agent DID is not a well-formed Ed25519 did:key.</summary>
    UnresolvableDid,

    /// <summary>Signature did not verify against the DID's public key.</summary>
    InvalidSignature,

    /// <summary>Timestamp outside the freshness window (too old, or too far in the future).</summary>
    TimestampExpired,

    /// <summary>Nonce already used (replay), or the nonce store was unavailable (fail closed).</summary>
    NonceAlreadyUsed,
}

/// <summary>
/// Outcome of verifying an agent signature. Deliberately carries no weight: how much a signed or
/// unsigned rating counts is endpoint policy, not a property of the cryptography.
/// </summary>
public sealed record AgentSignatureResult(AgentSignatureStatus Status)
{
    public bool IsVerified => Status == AgentSignatureStatus.Valid;

    /// <summary>
    /// True when the caller tried to sign and failed. Distinct from <see cref="AgentSignatureStatus.Missing"/>:
    /// an unsigned request degrades gracefully, a bad signature must not.
    /// </summary>
    public bool IsRejected => Status is not (AgentSignatureStatus.Valid or AgentSignatureStatus.Missing);

    public static readonly AgentSignatureResult Missing = new(AgentSignatureStatus.Missing);
    public static readonly AgentSignatureResult Valid = new(AgentSignatureStatus.Valid);
    public static AgentSignatureResult Failed(AgentSignatureStatus status) => new(status);
}

/// <summary>
/// Builds the exact byte string an agent signs. This is protocol, not an implementation detail:
/// every client (the MCP server, the framework integrations, third parties) must reproduce it
/// byte for byte, so it is defined once here and mirrored in the client libraries.
/// </summary>
public static class AgentSignaturePayload
{
    /// <summary>
    /// Domain separation. A signature made for another purpose (a receipt, a future v2 scheme)
    /// can never verify as a v1 request signature, and bumping this string is how the scheme
    /// migrates without a flag day.
    /// </summary>
    public const string Version = "trustscore-v1";

    /// <summary>
    /// Canonical signing input: fixed fields joined by newlines, in this order.
    ///
    /// <code>
    /// trustscore-v1
    /// api.trustscoreagent.com
    /// POST
    /// /v1/rate
    /// did:key:z6Mk...
    /// 2026-08-29T10:00:00Z
    /// &lt;nonce&gt;
    /// &lt;sha256(body) hex&gt;
    /// </code>
    ///
    /// Every field is a literal string both sides hold verbatim, so there is nothing to
    /// re-serialize and no key ordering to disagree on. In particular the request body is bound by
    /// its SHA-256 rather than by naming its fields: hashing the exact bytes covers the service,
    /// the metrics and the receipt at once, and avoids making clients reimplement the server's
    /// service-identifier normalisation just to sign a request.
    ///
    /// The <paramref name="audience"/> is the registry the request was addressed to. The registry
    /// is self-hostable, so without it a signature collected by one instance could be relayed to
    /// another and accepted there, letting a hostile operator forge ratings under the identities of
    /// agents that merely used it. No explicit comparison is needed: each server builds this string
    /// with its own identity, so a signature meant for a different one simply fails to verify.
    ///
    /// The timestamp is signed as the client wrote it (not reformatted), so the string that is
    /// verified is the string that was sent.
    /// </summary>
    public static string Canonicalize(
        string audience,
        string httpMethod,
        string requestPath,
        string agentDid,
        string timestamp,
        string nonce,
        byte[] body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        return string.Join('\n',
            Version,
            // Host names are case-insensitive, so normalise rather than reject a caller that
            // spelled the registry's own name differently.
            audience.ToLowerInvariant(),
            httpMethod.ToUpperInvariant(),
            requestPath,
            agentDid,
            timestamp,
            nonce,
            bodyHash);
    }

    /// <summary>UTF-8 bytes of <see cref="Canonicalize"/>, which is what Ed25519 actually signs.</summary>
    public static byte[] CanonicalBytes(
        string audience,
        string httpMethod,
        string requestPath,
        string agentDid,
        string timestamp,
        string nonce,
        byte[] body)
        => Encoding.UTF8.GetBytes(
            Canonicalize(audience, httpMethod, requestPath, agentDid, timestamp, nonce, body));
}

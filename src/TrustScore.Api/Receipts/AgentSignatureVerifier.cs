using System.Globalization;
using NSec.Cryptography;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Receipts;

/// <summary>
/// Verifies the per-request agent signature (X-Agent-Signature).
///
/// A receipt proves the *service* saw the interaction; this proves the *agent* is who it claims to
/// be. Without it, X-Agent-DID is a self-asserted string, so anyone can rate under another agent's
/// identity or mint unlimited identities for free.
/// </summary>
public sealed class AgentSignatureVerifier : IAgentSignatureVerifier
{
    // Same freshness window as receipts: long enough for a slow client, short enough that a
    // captured signature is useless once the nonce TTL outlives it.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);

    // Must outlive MaxAge + MaxClockSkew, otherwise a signature could become replayable again
    // while still inside its freshness window.
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);

    // An Ed25519 signature is 64 bytes (~86 base64url chars). The nonce has a floor as well as a
    // ceiling: a too-short nonce collides across agents, and a collision is a rejection.
    private const int MaxSignatureLength = 200;
    private const int MinNonceLength = 8;
    private const int MaxNonceLength = 100;
    private const int MaxTimestampLength = 40;

    /// <summary>
    /// Configuration key for this registry's canonical host. Set it in any deployment that other
    /// instances could relay signatures to: without it the audience falls back to the request's own
    /// Host header, which an attacker relaying a captured signature controls, so the binding stops
    /// being a defence.
    /// </summary>
    public const string AudienceConfigKey = "AgentSignature:Audience";

    private readonly ICacheService _cache;
    private readonly ILogger<AgentSignatureVerifier> _logger;
    private readonly string? _configuredAudience;

    public AgentSignatureVerifier(
        ICacheService cache,
        IConfiguration configuration,
        ILogger<AgentSignatureVerifier> logger)
    {
        _cache = cache;
        _logger = logger;
        _configuredAudience = configuration[AudienceConfigKey] is { Length: > 0 } audience
            ? audience.ToLowerInvariant()
            : null;

        if (_configuredAudience is null)
        {
            _logger.LogWarning(
                "{Key} is not configured; agent signatures fall back to the request Host, so a " +
                "signature captured by another registry could be relayed to this one.",
                AudienceConfigKey);
        }
    }

    public async Task<AgentSignatureResult> VerifyAsync(
        AgentSignatureHeaders headers,
        string requestHost,
        string httpMethod,
        string requestPath,
        byte[] body)
    {
        // 1. No signature headers at all: an unsigned legacy request. Not a failure; the endpoint
        // decides what an unsigned rating is worth. A *partially* signed request is malformed
        // though, otherwise omitting one header would be a way to downgrade into the lenient path.
        if (!headers.IsSignatureAttempted)
            return AgentSignatureResult.Missing;

        if (string.IsNullOrWhiteSpace(headers.AgentDid)
            || string.IsNullOrWhiteSpace(headers.Signature)
            || string.IsNullOrWhiteSpace(headers.Timestamp)
            || string.IsNullOrWhiteSpace(headers.Nonce))
        {
            return AgentSignatureResult.Failed(AgentSignatureStatus.Malformed);
        }

        if (headers.Signature.Length > MaxSignatureLength
            || headers.Timestamp.Length > MaxTimestampLength
            || headers.Nonce.Length is < MinNonceLength or > MaxNonceLength)
        {
            return AgentSignatureResult.Failed(AgentSignatureStatus.Malformed);
        }

        // 2. Freshness. Parsed invariant and assumed UTC so the window does not shift with the
        // host's culture or time zone. Rejecting future timestamps matters: a future-dated request
        // would otherwise stay valid long after its nonce entry expired.
        if (!DateTimeOffset.TryParse(headers.Timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt))
        {
            return AgentSignatureResult.Failed(AgentSignatureStatus.Malformed);
        }

        var age = DateTimeOffset.UtcNow - signedAt;
        if (age > MaxAge || age < -MaxClockSkew)
        {
            _logger.LogInformation("Agent signature timestamp out of range for {AgentDid}", headers.AgentDid);
            return AgentSignatureResult.Failed(AgentSignatureStatus.TimestampExpired);
        }

        // 3. Resolve the DID. No network call: a did:key carries its own key.
        var publicKeyBytes = DidKeyResolver.ResolvePublicKey(headers.AgentDid);
        if (publicKeyBytes is null)
        {
            _logger.LogWarning("Agent DID is not a resolvable Ed25519 did:key");
            return AgentSignatureResult.Failed(AgentSignatureStatus.UnresolvableDid);
        }

        // 4. Verify over the canonical payload, which binds this signature to this request:
        // a different body, path, method, timestamp or nonce yields a different signing input.
        byte[] signatureBytes;
        try
        {
            signatureBytes = Base64UrlDecodeBytes(headers.Signature);
        }
        catch (FormatException)
        {
            return AgentSignatureResult.Failed(AgentSignatureStatus.Malformed);
        }

        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);
            // The audience is this registry's own identity, never a value taken from the request
            // when one is configured. A signature addressed to a different registry therefore
            // produces a different signing input and fails here, with no explicit comparison.
            var signedData = AgentSignaturePayload.CanonicalBytes(
                _configuredAudience ?? requestHost,
                httpMethod, requestPath, headers.AgentDid, headers.Timestamp, headers.Nonce, body);

            if (!algorithm.Verify(publicKey, signedData, signatureBytes))
            {
                _logger.LogWarning("Invalid agent signature for {AgentDid}", headers.AgentDid);
                return AgentSignatureResult.Failed(AgentSignatureStatus.InvalidSignature);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent signature verification error");
            return AgentSignatureResult.Failed(AgentSignatureStatus.InvalidSignature);
        }

        // 5. Claim the nonce only AFTER the signature verifies, so a transient failure never burns
        // an honest agent's nonce. Scoped by DID and namespaced apart from receipt nonces, so one
        // agent cannot consume another's nonce and the two schemes cannot collide.
        var nonceKey = $"agent-nonce:{headers.AgentDid}:{headers.Nonce}";
        if (!await _cache.SetIfNotExistsAsync(nonceKey, "used", NonceTtl))
        {
            // The store answers "not claimed" both for a replay and when it is unreachable. Those
            // are different facts and must not share a status: reporting an outage as a replay
            // tells an honest client it did something wrong, and rejecting it turns a Redis
            // outage into an outage of every signing client.
            if (!await _cache.IsAvailableAsync())
            {
                _logger.LogWarning(
                    "Nonce store unavailable: accepting the rating from {AgentDid} as unsigned", headers.AgentDid);
                return AgentSignatureResult.Failed(AgentSignatureStatus.ReplayCheckUnavailable);
            }

            _logger.LogWarning("Agent nonce replay for {AgentDid}", headers.AgentDid);
            return AgentSignatureResult.Failed(AgentSignatureStatus.NonceAlreadyUsed);
        }

        return AgentSignatureResult.Valid;
    }

    private static byte[] Base64UrlDecodeBytes(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

using System.Text;
using System.Text.Json;
using NSec.Cryptography;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Receipts;

public sealed class ReceiptVerifier : IReceiptVerifier
{
    private readonly IDidResolver _didResolver;
    private readonly ICacheService _cache;
    private readonly ILogger<ReceiptVerifier> _logger;
    private static readonly TimeSpan MaxReceiptAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);

    public ReceiptVerifier(IDidResolver didResolver, ICacheService cache, ILogger<ReceiptVerifier> logger)
    {
        _didResolver = didResolver;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ReceiptVerificationResult> VerifyAsync(string jwt, string expectedServiceDid, string expectedAgentDid)
    {
        // 1. Parse the JWT (header.payload.signature)
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            _logger.LogWarning("Malformed JWT: expected 3 parts, got {Count}", parts.Length);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.MalformedJwt);
        }

        // 2. Decode the payload
        ReceiptPayload payload;
        try
        {
            var payloadJson = Base64UrlDecode(parts[1]);
            payload = JsonSerializer.Deserialize<ReceiptPayload>(payloadJson, JsonOptions)
                ?? throw new JsonException("Null payload");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode receipt payload");
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.MalformedJwt);
        }

        // 3. Check coherence: service_did must match
        if (payload.ServiceDid != expectedServiceDid)
        {
            _logger.LogWarning("Receipt service_did mismatch: expected {Expected}, got {Got}",
                expectedServiceDid, payload.ServiceDid);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.InvalidSignature);
        }

        // 4. Check timestamp: must be recent and not in the future (a future-dated receipt would
        // otherwise have an unbounded freshness window once its nonce TTL expires).
        if (!DateTimeOffset.TryParse(payload.Timestamp, out var receiptTime))
        {
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.MalformedJwt);
        }
        var age = DateTimeOffset.UtcNow - receiptTime;
        if (age > MaxReceiptAge || age < -MaxClockSkew)
        {
            _logger.LogInformation("Receipt timestamp out of range for {ServiceDid}", expectedServiceDid);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.TimestampExpired);
        }

        // 5. Resolve the service DID to get the public key
        var publicKeyBytes = await _didResolver.ResolvePublicKeyAsync(payload.ServiceDid);
        if (publicKeyBytes is null)
        {
            _logger.LogWarning("DID resolution failed for {ServiceDid}", payload.ServiceDid);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.DidResolutionFailed);
        }

        // 6. Verify the Ed25519 signature
        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);

            var signedData = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Base64UrlDecodeBytes(parts[2]);

            var isValid = algorithm.Verify(publicKey, signedData, signature);

            if (!isValid)
            {
                _logger.LogWarning("Invalid Ed25519 signature for receipt from {ServiceDid}", payload.ServiceDid);
                return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.InvalidSignature);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signature verification error for {ServiceDid}", payload.ServiceDid);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.InvalidSignature);
        }

        // 6b. The receipt must attest the agent that is actually submitting it. Without this check
        // the receipt is a bearer token: anyone who observes it (it travels in the X-Trust-Receipt
        // header) could replay it under their own DID to gain verified weight and burn the real
        // agent's nonce. Checked AFTER the signature but BEFORE claiming the nonce, so a mismatched
        // attempt neither counts nor consumes the legitimate agent's nonce.
        if (payload.AgentDid != expectedAgentDid)
        {
            _logger.LogWarning("Receipt agent_did mismatch for {ServiceDid}: signed {Signed}, submitted by {Submitter}",
                payload.ServiceDid, payload.AgentDid, expectedAgentDid);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.InvalidSignature);
        }

        // 7. Claim the nonce AFTER the signature is verified (anti-replay). Claiming earlier would
        // burn a legitimate receipt's nonce on a transient failure (e.g. DID resolution timeout),
        // wrongly rejecting an honest re-submission. The SETNX is still atomic, so concurrent
        // submissions of the same valid receipt are correctly de-duplicated. Fail-closed if Redis
        // is down (reject rather than risk a replay).
        var nonceKey = $"nonce:{payload.Nonce}";
        var nonceClaimed = await _cache.SetIfNotExistsAsync(nonceKey, "used", NonceTtl);
        if (!nonceClaimed)
        {
            _logger.LogWarning("Nonce replay or Redis unavailable: {Nonce} for {ServiceDid}", payload.Nonce, expectedServiceDid);
            return ReceiptVerificationResult.Rejected(ReceiptVerificationStatus.NonceAlreadyUsed);
        }

        _logger.LogInformation("Receipt verified for {ServiceDid} from {AgentDid}",
            payload.ServiceDid, payload.AgentDid);

        return ReceiptVerificationResult.Verified(payload);
    }

    private static string Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

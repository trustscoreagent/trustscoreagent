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
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);

    public ReceiptVerifier(IDidResolver didResolver, ICacheService cache, ILogger<ReceiptVerifier> logger)
    {
        _didResolver = didResolver;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ReceiptVerificationResult> VerifyAsync(string jwt, string expectedServiceDid)
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

        // 4. Check timestamp (must be within 5 minutes)
        if (DateTimeOffset.TryParse(payload.Timestamp, out var receiptTime))
        {
            if (DateTimeOffset.UtcNow - receiptTime > MaxReceiptAge)
            {
                _logger.LogInformation("Receipt timestamp expired for {ServiceDid}", expectedServiceDid);
                return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.TimestampExpired);
            }
        }
        else
        {
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.MalformedJwt);
        }

        // 5. Check nonce (anti-replay)
        var nonceKey = $"nonce:{payload.Nonce}";
        var existingNonce = await _cache.GetAsync(nonceKey);
        if (existingNonce is not null)
        {
            _logger.LogWarning("Nonce replay detected: {Nonce} for {ServiceDid}", payload.Nonce, expectedServiceDid);
            return ReceiptVerificationResult.Rejected(ReceiptVerificationStatus.NonceAlreadyUsed);
        }

        // 6. Resolve the service DID to get the public key
        var publicKeyBytes = await _didResolver.ResolvePublicKeyAsync(payload.ServiceDid);
        if (publicKeyBytes is null)
        {
            _logger.LogWarning("DID resolution failed for {ServiceDid}", payload.ServiceDid);
            return ReceiptVerificationResult.Failed(ReceiptVerificationStatus.DidResolutionFailed);
        }

        // 7. Verify the Ed25519 signature
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

        // 8. Mark nonce as used (after successful verification)
        await _cache.SetAsync(nonceKey, "used", NonceTtl);

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

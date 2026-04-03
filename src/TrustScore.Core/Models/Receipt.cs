namespace TrustScore.Core.Models;

public sealed class ReceiptPayload
{
    public string Version { get; init; } = "1.0";
    public required string ServiceDid { get; init; }
    public required string AgentDid { get; init; }
    public required string Timestamp { get; init; }
    public required string Nonce { get; init; }
    public required string Endpoint { get; init; }
    public required string Method { get; init; }
    public int StatusCode { get; init; }
    public string? ResponseHash { get; init; }  // Optional: SHA256 of response body
    public int? LatencyMs { get; init; }         // Optional: processing time
}

public enum ReceiptVerificationStatus
{
    Valid,
    InvalidSignature,
    NonceAlreadyUsed,
    TimestampExpired,
    DidResolutionFailed,
    MalformedJwt,
}

public sealed record ReceiptVerificationResult(
    ReceiptVerificationStatus Status,
    ReceiptPayload? Payload,
    double Weight
)
{
    public bool IsVerified => Status == ReceiptVerificationStatus.Valid;

    public static ReceiptVerificationResult Verified(ReceiptPayload payload)
        => new(ReceiptVerificationStatus.Valid, payload, 1.0);

    public static ReceiptVerificationResult Failed(ReceiptVerificationStatus status)
        => new(status, null, 0.3);

    public static ReceiptVerificationResult Rejected(ReceiptVerificationStatus status)
        => new(status, null, 0.0);
}

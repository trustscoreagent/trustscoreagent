namespace TrustScore.Core.Models;

/// <summary>
/// Whether a measured response counts as the service answering correctly, for the availability
/// dimension and for EigenTrust's consensus on it.
///
/// A 2xx always does. So does a 402 whose x402 payment challenge was valid
/// (<c>schema_valid = true</c>): for a paid endpoint that is the protocol's correct answer to a
/// request that carries no payment, and the only one a caller gets before deciding to pay. A 402
/// without a valid challenge stays neutral, like any other 4xx; 5xx and transport failures count
/// against the service.
/// </summary>
public static class ResponseOutcome
{
    public static bool IsSuccess(int statusCode, bool? schemaValid) =>
        statusCode is >= 200 and < 300 || (statusCode == 402 && schemaValid == true);

    public static bool IsFailure(int statusCode) => statusCode >= 500;
}

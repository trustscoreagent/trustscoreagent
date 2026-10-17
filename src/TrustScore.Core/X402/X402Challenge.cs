using System.Text;
using System.Text.Json;

namespace TrustScore.Core.X402;

/// <summary>One way to pay for an x402 resource, as its 402 challenge offers it.</summary>
public sealed record X402Requirement(string Scheme, string Network, string Amount, string Asset, string PayTo);

/// <summary>
/// The payment challenge an x402 endpoint answers with when a request carries no payment: the
/// base64 JSON of the <c>PAYMENT-REQUIRED</c> header (x402 v2), or the JSON body (v1). Only the
/// fields a payer needs to decide are kept; a challenge is valid when it names a known version and
/// at least one complete way to pay.
/// </summary>
public sealed record X402Challenge(int Version, IReadOnlyList<X402Requirement> Accepts)
{
    public const string HeaderName = "PAYMENT-REQUIRED";

    // A challenge is a few hundred bytes; anything far larger is not one.
    private const int MaxChallengeBytes = 64 * 1024;

    /// <summary>
    /// Parses the challenge from the header when present, else from the body. Returns null when
    /// neither holds a valid one, which the probe records as a non-conformant answer.
    /// </summary>
    public static X402Challenge? Parse(string? paymentRequiredHeader, string? body)
    {
        if (!string.IsNullOrWhiteSpace(paymentRequiredHeader))
            return FromJson(DecodeBase64(paymentRequiredHeader.Trim()));
        return FromJson(body);
    }

    private static string? DecodeBase64(string value)
    {
        if (value.Length > MaxChallengeBytes * 2) return null;
        try
        {
            // Accept both alphabets and missing padding: servers differ.
            var s = value.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static X402Challenge? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxChallengeBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("x402Version", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var version) || version is not (1 or 2)
                || !root.TryGetProperty("accepts", out var accepts) || accepts.ValueKind != JsonValueKind.Array)
                return null;

            var requirements = new List<X402Requirement>();
            foreach (var a in accepts.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                // v2 names the price "amount"; v1 "maxAmountRequired".
                var amount = Text(a, version == 2 ? "amount" : "maxAmountRequired") ?? Text(a, "amount");
                var scheme = Text(a, "scheme");
                var network = Text(a, "network");
                var asset = Text(a, "asset");
                var payTo = Text(a, "payTo");
                if (scheme is null || network is null || asset is null || payTo is null
                    || amount is null || !amount.All(char.IsAsciiDigit))
                    continue;
                requirements.Add(new X402Requirement(scheme, network, amount, asset, payTo));
            }
            return requirements.Count == 0 ? null : new X402Challenge(version, requirements);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        && p.GetString() is { Length: > 0 and <= 256 } s
            ? s
            : null;
}

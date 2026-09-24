using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TrustScore.Core.Audit;

/// <summary>
/// The fields of a rating that its audit leaf commits to, and how that leaf is hashed.
///
/// v1 leaves (every rating stored before v2) commit to <c>(id, service, created_at)</c> only, so
/// they prove a rating existed but not what it reported. v2 leaves commit to everything that
/// feeds the score: the measured metrics, the subjective score, whether a receipt and a signature
/// were verified, and the weight the rating was counted at. Changing any of those after the fact
/// changes the leaf and breaks the anchored root.
///
/// Deliberately NOT committed, as before: the agent DID and the free-text comment. Both can be
/// personal data and must stay erasable; a hash in a published tree cannot be erased.
///
/// Existing ratings are never re-hashed as v2. Their content was not committed when they were
/// written, and computing a v2 leaf from today's database would present current values as if they
/// had been committed back then.
/// </summary>
public sealed record RatingLeaf(
    Guid Id,
    string ServiceDid,
    DateTimeOffset CreatedAt,
    int LeafVersion,
    int StatusCode,
    int LatencyMs,
    int? ResponseSizeBytes,
    bool? SchemaValid,
    int? QualityScore,
    bool HasReceipt,
    bool ReceiptVerified,
    bool SignatureVerified,
    double Weight)
{
    public const int CurrentVersion = 2;

    /// <summary>Domain string opening every v2 preimage, so a leaf cannot be mistaken for any other hashed record.</summary>
    public const string V2Domain = "trustscore-leaf-v2";

    private const byte LeafPrefix = 0x00;

    /// <summary>Decimal places the weight is committed at (and rounded to before it is stored).</summary>
    public const int WeightDecimals = 6;

    /// <summary>Rounds a weight to what a v2 leaf commits to, so the stored value and the committed one agree exactly.</summary>
    public static double NormalizeWeight(double weight) =>
        Math.Round(weight, WeightDecimals, MidpointRounding.AwayFromZero);

    /// <summary>created_at as a v1 leaf spelled it (round-trip "O" format).</summary>
    public string V1Timestamp => CreatedAt.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>created_at as a v2 leaf spells it: UTC, microseconds, 'Z'.</summary>
    public string V2Timestamp =>
        CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The v2 preimage, before the 0x00 prefix: the domain string and then each field on its own
    /// line, in this fixed order. Nulls are empty lines, booleans are <c>true</c>/<c>false</c>,
    /// integers are plain decimal, the weight has exactly six decimals.
    /// <code>
    /// trustscore-leaf-v2
    /// 3f2b...-...-...    id, lowercase, hyphenated
    /// api.example.com    service
    /// 2026-09-24T16:02:55.123456Z
    /// 200                status_code
    /// 143                latency_ms
    /// 2048               response_size_bytes (or empty)
    /// true               schema_valid (or empty)
    /// 4                  quality_score (or empty)
    /// false              has_receipt
    /// false              receipt_verified
    /// true               signature_verified
    /// 0.300000           weight
    /// </code>
    /// </summary>
    public string CanonicalV2()
    {
        var fields = new[]
        {
            V2Domain,
            Id.ToString("D"),
            ServiceDid,
            V2Timestamp,
            Int(StatusCode),
            Int(LatencyMs),
            ResponseSizeBytes is { } size ? Int(size) : "",
            SchemaValid is { } valid ? Bool(valid) : "",
            QualityScore is { } quality ? Int(quality) : "",
            Bool(HasReceipt),
            Bool(ReceiptVerified),
            Bool(SignatureVerified),
            NormalizeWeight(Weight).ToString("F" + WeightDecimals, CultureInfo.InvariantCulture),
        };

        // A newline inside a field would let two different records share a preimage. Service ids
        // are normalized and cannot contain one, so this only fires on a bug.
        if (fields.Any(f => f.Contains('\n')))
            throw new ArgumentException("A committed rating field contains a newline.");

        return string.Join('\n', fields);
    }

    /// <summary>The leaf hash for this rating under its own <see cref="LeafVersion"/>.</summary>
    public byte[] Hash() => LeafVersion switch
    {
        1 => MerkleTree.ComputeLeafHash(Id, ServiceDid, CreatedAt),
        2 => HashV2(),
        _ => throw new InvalidOperationException($"Unknown leaf version {LeafVersion}"),
    };

    public string HashHex() => Convert.ToHexString(Hash()).ToLowerInvariant();

    private byte[] HashV2()
    {
        var text = Encoding.UTF8.GetBytes(CanonicalV2());
        var preimage = new byte[1 + text.Length];
        preimage[0] = LeafPrefix;
        text.CopyTo(preimage, 1);
        return SHA256.HashData(preimage);
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Bool(bool value) => value ? "true" : "false";
}

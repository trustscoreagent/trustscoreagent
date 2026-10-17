namespace TrustScore.Core.X402;

/// <summary>
/// The payment terms an x402 endpoint was last seen asking for, and how stable they have been:
/// when its payee address (<c>payTo</c>) or its price last changed, and how many times. A payee
/// that just changed on an endpoint agents pay every day is the first thing a payer should know
/// before sending money.
/// </summary>
public sealed record X402TermsRecord(
    string ServiceDid,
    int Version,
    IReadOnlyList<X402Requirement> Accepts,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? PayToChangedAt,
    int PayToChanges,
    DateTimeOffset? PriceChangedAt,
    int PriceChanges);

/// <summary>What changed between two challenges of the same endpoint.</summary>
public readonly record struct X402TermsChange(bool PayTo, bool Price)
{
    public bool Any => PayTo || Price;
}

public static class X402Terms
{
    /// <summary>At most this many ways to pay are kept per endpoint; real challenges list one to a few.</summary>
    public const int MaxStoredRequirements = 16;

    /// <summary>
    /// Compares two challenges' ways to pay, ignoring their order. The payee changed when the set
    /// of (network, payTo) differs; the price changed when the set of (scheme, network, asset,
    /// amount) differs, so a new asset or network counts as a price change too.
    /// </summary>
    public static X402TermsChange Compare(IReadOnlyList<X402Requirement> previous, IReadOnlyList<X402Requirement> current) =>
        new(!PayToSet(previous).SetEquals(PayToSet(current)), !PriceSet(previous).SetEquals(PriceSet(current)));

    /// <summary>The requirements to store: deduplicated, in a stable order, capped.</summary>
    public static IReadOnlyList<X402Requirement> Canonical(IReadOnlyList<X402Requirement> accepts) =>
        accepts
            .DistinctBy(Key)
            .OrderBy(Key, StringComparer.Ordinal)
            .Take(MaxStoredRequirements)
            .ToList();

    private static HashSet<string> PayToSet(IReadOnlyList<X402Requirement> accepts) =>
        accepts.Select(a => $"{a.Network}|{Address(a.PayTo)}").ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> PriceSet(IReadOnlyList<X402Requirement> accepts) =>
        accepts.Select(a => $"{a.Scheme}|{a.Network}|{Address(a.Asset)}|{a.Amount.TrimStart('0')}")
            .ToHashSet(StringComparer.Ordinal);

    private static string Key(X402Requirement a) =>
        $"{a.Scheme}|{a.Network}|{Address(a.Asset)}|{a.Amount.TrimStart('0')}|{Address(a.PayTo)}";

    // EVM addresses are hex, and servers send them in either case or checksummed: the same
    // address. Other chains' addresses (Solana's base58) are case-sensitive and kept as is.
    private static string Address(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.ToLowerInvariant() : value;
}

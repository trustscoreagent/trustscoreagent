namespace TrustScore.Api.Receipts;

/// <summary>
/// Resolves a did:key identifier to its Ed25519 public key.
///
/// Unlike did:web, a did:key *contains* the key: the identifier is the multibase encoding of the
/// key itself, so resolution is a pure decode with no network call, no cache, and no SSRF surface.
/// That is what makes it the right method for agent identity: an agent has no domain to host a DID
/// document at, it just generates a keypair locally and its DID is its public key.
///
/// Deliberately not an <see cref="Core.Interfaces.IDidResolver"/>: that interface is async because
/// did:web fetches over HTTP, and registering a second implementation would make the DI resolution
/// for receipts ambiguous. This is a pure function, so it needs neither.
/// </summary>
internal static class DidKeyResolver
{
    private const string Prefix = "did:key:z"; // 'z' = base58btc multibase

    // A did:key for Ed25519 decodes to 34 bytes, so its base58 form is ~48 characters. Cap the
    // input well above that but far below Base58.Decode's own 256-char limit: anything longer is
    // not a key we can use, and rejecting early avoids the BigInteger work entirely.
    private const int MaxDidLength = 128;

    /// <summary>
    /// Returns the raw 32-byte Ed25519 public key encoded in the DID, or null if the DID is not a
    /// well-formed Ed25519 did:key.
    /// </summary>
    public static byte[]? ResolvePublicKey(string? did)
    {
        if (string.IsNullOrEmpty(did) || did.Length > MaxDidLength)
            return null;

        // Case-sensitive: base58btc distinguishes case, and the DID string is the identity.
        if (!did.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        // Reject DID URLs (fragments, paths, queries). "did:key:zX" and "did:key:zX#zX" designate
        // the same key but are different strings, and the agent DID is used verbatim as an identity
        // key (rate limiting, EigenTrust, Merkle leaves). Accepting both spellings would let one
        // agent hold two reputations, so only the bare, canonical DID is valid here.
        if (did.AsSpan(Prefix.Length).IndexOfAny('#', '/', '?') >= 0)
            return null;

        var encoded = did[Prefix.Length..];
        if (encoded.Length == 0)
            return null;

        byte[] decoded;
        try
        {
            decoded = Base58.Decode(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        // Strict, unlike did:web: the multicodec prefix must be present and must say Ed25519.
        // Without this check a did:key encoding a different algorithm could be accepted and its
        // bytes used as if they were an Ed25519 key.
        if (!Ed25519KeyCodec.HasMulticodecPrefix(decoded))
            return null;

        return decoded[2..];
    }
}

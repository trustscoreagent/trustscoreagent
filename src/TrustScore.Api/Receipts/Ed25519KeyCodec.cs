namespace TrustScore.Api.Receipts;

/// <summary>
/// Shared encoding helpers for Ed25519 public keys. Both DID methods we support carry the same
/// key material, just wrapped differently: did:web embeds it in a DID document's verification
/// method, did:key encodes it directly in the identifier. Keeping the multicodec/base58 handling
/// in one place means a fix (e.g. a length check) applies to both resolvers.
/// </summary>
internal static class Ed25519KeyCodec
{
    /// <summary>The multicodec prefix identifying an Ed25519 public key (0xED, varint-encoded).</summary>
    public const byte MulticodecByte0 = 0xED;
    public const byte MulticodecByte1 = 0x01;

    /// <summary>Raw Ed25519 public keys are always 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>
    /// Returns the raw 32-byte Ed25519 key, stripping the 0xED 0x01 multicodec prefix if present
    /// (as in Ed25519VerificationKey2020's multibase encoding). Returns null on any other length.
    /// Lenient by design: did:web documents in the wild use both encodings. Callers that must
    /// know the key really is Ed25519 (rather than another algorithm of the same length) have to
    /// check the multicodec prefix themselves; see <see cref="HasMulticodecPrefix"/>.
    /// </summary>
    public static byte[]? Normalize(byte[] keyBytes)
    {
        if (HasMulticodecPrefix(keyBytes))
            return keyBytes[2..];
        if (keyBytes.Length == KeyLength)
            return keyBytes;
        return null;
    }

    /// <summary>
    /// True if the bytes are a multicodec-prefixed Ed25519 key (0xED 0x01 followed by 32 bytes).
    /// </summary>
    public static bool HasMulticodecPrefix(byte[] keyBytes) =>
        keyBytes.Length == KeyLength + 2
        && keyBytes[0] == MulticodecByte0
        && keyBytes[1] == MulticodecByte1;
}

/// <summary>
/// base64url (RFC 4648 §5) as used by JWTs, JWKs and agent signatures: '-' and '_' in place of
/// '+' and '/', padding optional. One implementation, so every place that decodes it rejects the
/// same malformed inputs the same way.
/// </summary>
internal static class Base64Url
{
    /// <summary>Decodes base64url, with or without padding.</summary>
    /// <exception cref="FormatException">The input is not valid base64url.</exception>
    public static byte[] Decode(string input)
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

/// <summary>
/// Minimal Base58 decoder for multibase-encoded public keys.
/// </summary>
internal static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static byte[] Decode(string input)
    {
        if (input.Length > 256)
            throw new FormatException("Base58 input too long (max 256 characters)");

        var bi = System.Numerics.BigInteger.Zero;
        foreach (var c in input)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) throw new FormatException($"Invalid Base58 character: {c}");
            bi = bi * 58 + index;
        }

        var bytes = bi.ToByteArray(isUnsigned: true, isBigEndian: true);

        // Count leading '1's (which represent leading zero bytes)
        var leadingZeros = input.TakeWhile(c => c == '1').Count();
        if (leadingZeros > 0)
        {
            var result = new byte[leadingZeros + bytes.Length];
            bytes.CopyTo(result, leadingZeros);
            return result;
        }

        return bytes;
    }
}

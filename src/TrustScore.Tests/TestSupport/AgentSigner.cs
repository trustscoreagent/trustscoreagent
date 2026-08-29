using System.Numerics;
using System.Text;
using NSec.Cryptography;
using TrustScore.Core.Models;

namespace TrustScore.Tests.TestSupport;

/// <summary>
/// Client-side half of the agent signature scheme, used by tests to sign requests exactly as a
/// real agent would. Kept in one place so the unit and integration suites cannot drift apart on
/// the encoding, which is precisely the class of bug these tests exist to catch.
/// </summary>
internal static class AgentSigner
{
    /// <summary>Builds the did:key for a keypair: base58btc of 0xED01 + the raw public key.</summary>
    public static string DidKeyFor(Key key)
    {
        var raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return "did:key:z" + Base58Encode(new byte[] { 0xED, 0x01 }.Concat(raw).ToArray());
    }

    /// <summary>
    /// The audience the integration tests sign for. TestServer reports this Host, and no canonical
    /// audience is configured in tests, so the verifier falls back to it.
    /// </summary>
    public const string TestAudience = "localhost";

    public static AgentSignatureHeaders Sign(
        Key signingKey,
        byte[] body,
        string method = "POST",
        string path = "/v1/rate",
        string? agentDid = null,
        DateTimeOffset? timestamp = null,
        string? nonce = null,
        string audience = TestAudience)
    {
        var did = agentDid ?? DidKeyFor(signingKey);
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToString("o");
        var n = nonce ?? Guid.NewGuid().ToString("N");

        var payload = AgentSignaturePayload.CanonicalBytes(audience, method, path, did, ts, n, body);
        var signature = SignatureAlgorithm.Ed25519.Sign(signingKey, payload);

        return new AgentSignatureHeaders(did, Base64Url(signature), ts, n);
    }

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Inverse of the production base58btc decoder.</summary>
    public static string Base58Encode(byte[] data)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var sb = new StringBuilder();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            sb.Insert(0, alphabet[(int)remainder]);
        }
        foreach (var b in data)
        {
            if (b != 0) break;
            sb.Insert(0, '1');
        }
        return sb.ToString();
    }
}

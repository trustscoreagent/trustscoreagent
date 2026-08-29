using System.Numerics;
using System.Text;
using FluentAssertions;
using TrustScore.Api.Receipts;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// did:key is how agents will identify themselves when signing ratings (X-Agent-Signature), so a
/// permissive decode here would let a caller claim a key it does not hold. These tests pin the
/// strict behaviour: only a bare, canonical, Ed25519-multicodec did:key resolves.
/// </summary>
public class DidKeyResolverTests
{
    // W3C did:key test vector: z6MkiTBz... is the base58btc encoding of 0xED01 + this key.
    private const string ValidDid = "did:key:z6MkiTBz1ymuepAQ4HEHYSF1H8quG5GLVVQR3djdX3mDooWp";
    private static readonly byte[] ExpectedKey =
        Convert.FromHexString("3b6a27bcceb6a42d62a3a8d02a6f0d73653215771de243a63ac048a18b59da29");

    [Fact]
    public void ResolvePublicKey_ReturnsRawKey_ForValidEd25519DidKey()
    {
        var key = DidKeyResolver.ResolvePublicKey(ValidDid);

        key.Should().NotBeNull();
        key!.Should().HaveCount(32).And.Equal(ExpectedKey);
    }

    [Fact]
    public void ResolvePublicKey_MatchesTheEncodingOfPrefixPlusKey()
    {
        // Self-check on the published vector: encoding 0xED01 + key must reproduce the DID above.
        // If this ever fails, the test vector and the decoder disagree and every other case here
        // would be testing the wrong bytes.
        var multicodec = new byte[] { 0xED, 0x01 }.Concat(ExpectedKey).ToArray();

        var did = "did:key:z" + Base58Encode(multicodec);

        did.Should().Be(ValidDid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("did:web:api.example.com")]                      // wrong method
    [InlineData("did:key:6MkiTBz1ymuepAQ4HEHYSF1H8quG5GLVVQR3djdX3mDooWp")] // missing 'z' multibase
    [InlineData("DID:KEY:z6MkiTBz1ymuepAQ4HEHYSF1H8quG5GLVVQR3djdX3mDooWp")] // wrong case
    [InlineData("did:key:z")]                                    // empty payload
    [InlineData("did:key:z0OIl")]                                // characters outside base58
    public void ResolvePublicKey_ReturnsNull_ForMalformedDids(string? did)
    {
        DidKeyResolver.ResolvePublicKey(did).Should().BeNull();
    }

    [Theory]
    [InlineData("#z6MkiTBz1ymuepAQ4HEHYSF1H8quG5GLVVQR3djdX3mDooWp")]
    [InlineData("/path")]
    [InlineData("?query=1")]
    public void ResolvePublicKey_ReturnsNull_ForDidUrls(string suffix)
    {
        // A fragment/path form designates the same key but is a different string. The agent DID is
        // used verbatim as an identity key (rate limiting, EigenTrust, Merkle leaves), so accepting
        // both spellings would let one agent hold two separate reputations.
        DidKeyResolver.ResolvePublicKey(ValidDid + suffix).Should().BeNull();
    }

    [Fact]
    public void ResolvePublicKey_ReturnsNull_ForNonEd25519Multicodec()
    {
        // 0xE7 0x01 is secp256k1. Without the strict prefix check its bytes could be handed back
        // and verified as if they were an Ed25519 key.
        var secp = new byte[] { 0xE7, 0x01 }.Concat(ExpectedKey).ToArray();

        DidKeyResolver.ResolvePublicKey("did:key:z" + Base58Encode(secp)).Should().BeNull();
    }

    [Fact]
    public void ResolvePublicKey_ReturnsNull_WhenMulticodecPrefixIsMissing()
    {
        // A bare 32-byte payload is tolerated for did:web documents but is not a valid did:key:
        // nothing in it states which algorithm the key belongs to.
        DidKeyResolver.ResolvePublicKey("did:key:z" + Base58Encode(ExpectedKey)).Should().BeNull();
    }

    [Fact]
    public void ResolvePublicKey_ReturnsNull_ForWrongKeyLength()
    {
        var shortKey = new byte[] { 0xED, 0x01 }.Concat(ExpectedKey[..31]).ToArray();

        DidKeyResolver.ResolvePublicKey("did:key:z" + Base58Encode(shortKey)).Should().BeNull();
    }

    [Fact]
    public void ResolvePublicKey_ReturnsNull_ForOverlongInput()
    {
        DidKeyResolver.ResolvePublicKey("did:key:z" + new string('1', 200)).Should().BeNull();
    }

    /// <summary>Test-only base58btc encoder, the inverse of the production decoder.</summary>
    private static string Base58Encode(byte[] data)
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

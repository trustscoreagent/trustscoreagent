using System.Text;
using System.Text.Json;
using FluentAssertions;
using TrustScore.Api.Receipts;
using Xunit;

namespace TrustScore.Tests.Unit;

public class DidKeyExtractionTests
{
    // RFC 8032 / did:key test vector: the multibase z6MkiTBz... encodes the 0xED01 multicodec
    // prefix followed by this 32-byte Ed25519 public key.
    private const string MultibaseKey = "z6MkiTBz1ymuepAQ4HEHYSF1H8quG5GLVVQR3djdX3mDooWp";
    private static readonly byte[] ExpectedKey =
        Convert.FromHexString("3b6a27bcceb6a42d62a3a8d02a6f0d73653215771de243a63ac048a18b59da29");

    [Fact]
    public void ExtractEd25519Key_StripsMulticodecPrefix_From2020Multibase()
    {
        var method = Parse($$"""
            { "type": "Ed25519VerificationKey2020", "publicKeyMultibase": "{{MultibaseKey}}" }
            """);

        var key = DidWebResolver.ExtractEd25519Key(method);

        key.Should().NotBeNull();
        key!.Should().HaveCount(32).And.Equal(ExpectedKey);
    }

    [Fact]
    public void ExtractEd25519Key_DecodesJwk_ForJsonWebKey2020()
    {
        var x = Base64UrlEncode(ExpectedKey);
        var method = Parse($$"""
            { "type": "JsonWebKey2020", "publicKeyJwk": { "kty": "OKP", "crv": "Ed25519", "x": "{{x}}" } }
            """);

        var key = DidWebResolver.ExtractEd25519Key(method);

        key.Should().Equal(ExpectedKey);
    }

    [Fact]
    public void ExtractEd25519Key_AcceptsRawBase64_ForBackwardsCompatibility()
    {
        var method = Parse($$"""
            { "type": "Ed25519VerificationKey2020", "publicKeyBase64": "{{Convert.ToBase64String(ExpectedKey)}}" }
            """);

        DidWebResolver.ExtractEd25519Key(method).Should().Equal(ExpectedKey);
    }

    [Fact]
    public void ExtractEd25519Key_ReturnsNull_ForWrongLengthKey()
    {
        // 33 bytes: neither a raw key nor a multicodec-prefixed one.
        var method = Parse($$"""
            { "type": "Ed25519VerificationKey2020", "publicKeyBase64": "{{Convert.ToBase64String(new byte[33])}}" }
            """);

        DidWebResolver.ExtractEd25519Key(method).Should().BeNull();
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

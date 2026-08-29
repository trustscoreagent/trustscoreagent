using System.Text;
using FluentAssertions;
using NSec.Cryptography;
using TrustScore.Api.Receipts;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// Cross-language interop: this signature was produced by the TypeScript MCP client, not by .NET.
/// The two implementations have to agree byte for byte on the did:key derivation, the canonical
/// payload and the base64url encoding, and nothing else in the suite would catch them drifting
/// apart, because every other test signs and verifies with the same code.
///
/// The vector holds only public material (the did:key carries the public key), so no private key
/// is committed. Regenerate it by signing with the client's signRequest() if the scheme changes,
/// which is exactly the moment this test is supposed to fail.
/// </summary>
public class AgentSignatureInteropTests
{
    private const string Did = "did:key:z6MkonQJwPi5bfkwfADWxacWjxzneWtKA6bQ9SVaH275jWie";
    private const string Audience = "api.trustscoreagent.com";
    private const string Timestamp = "2026-08-29T12:00:00.000Z";
    private const string Nonce = "crosslangvector01";
    private const string Body = """{"service":"api.example.com","metrics":{"status_code":200,"latency_ms":100}}""";
    private const string Signature =
        "XdZtLK7Te9aUeWP7GgqMegT4u3Ky0S3SqlHrce0Ep6cXfVZfE8ZLgOayc0lkWSK0vSVhH0iMNF7W9631zHzaCA";

    [Fact]
    public void SignatureFromTheTypeScriptClient_VerifiesOnTheServer()
    {
        var publicKeyBytes = DidKeyResolver.ResolvePublicKey(Did);
        publicKeyBytes.Should().NotBeNull("the client's did:key must resolve with the server's decoder");

        var canonical = AgentSignaturePayload.CanonicalBytes(
            Audience, "POST", "/v1/rate", Did, Timestamp, Nonce, Encoding.UTF8.GetBytes(Body));

        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, publicKeyBytes!, KeyBlobFormat.RawPublicKey);

        algorithm.Verify(publicKey, canonical, Base64UrlDecode(Signature))
            .Should().BeTrue("the client and the server must build the same signing input");
    }

    [Fact]
    public void ADifferentBody_DoesNotVerify_AgainstTheSameVector()
    {
        // Guards against the test passing for the wrong reason (e.g. a verifier that ignores input).
        var publicKeyBytes = DidKeyResolver.ResolvePublicKey(Did)!;
        var canonical = AgentSignaturePayload.CanonicalBytes(
            Audience, "POST", "/v1/rate", Did, Timestamp, Nonce, Encoding.UTF8.GetBytes(Body + " "));

        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);

        algorithm.Verify(publicKey, canonical, Base64UrlDecode(Signature)).Should().BeFalse();
    }

    private static byte[] Base64UrlDecode(string input)
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

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using TrustScore.Api.Receipts;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using TrustScore.Tests.Integration;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// End-to-end tests of ReceiptVerifier.VerifyAsync with REAL Ed25519-signed JWTs — the full
/// production pipeline: parse → service_did → timestamp → DID resolution → signature →
/// agent binding → nonce. Previously this path had zero coverage (2026-07 audit, test debt).
/// </summary>
public class ReceiptVerifierVerifyAsyncTests
{
    private const string ServiceDid = "did:web:svc.example.com";
    private const string AgentDid = "did:web:agent.example.com";

    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Key _serviceKey = Key.Create(SignatureAlgorithm.Ed25519);

    private ReceiptVerifier CreateVerifier(FakeCacheService? cache = null, byte[]? resolvedKey = null)
    {
        var key = resolvedKey ?? _serviceKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return new ReceiptVerifier(
            new StubDidResolver(_ => key),
            cache ?? new FakeCacheService(),
            NullLogger<ReceiptVerifier>.Instance);
    }

    private static ReceiptPayload MakePayload(
        string serviceDid = ServiceDid, string agentDid = AgentDid,
        DateTimeOffset? timestamp = null, string? nonce = null) => new()
        {
            ServiceDid = serviceDid,
            AgentDid = agentDid,
            Timestamp = (timestamp ?? DateTimeOffset.UtcNow).ToString("o"),
            Nonce = nonce ?? Guid.NewGuid().ToString(),
            Endpoint = "/v1/translate",
            Method = "POST",
            StatusCode = 200,
        };

    private static string BuildJwt(ReceiptPayload payload, Key signingKey)
    {
        var header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"EdDSA","typ":"JWT"}"""));
        var body = B64Url(JsonSerializer.SerializeToUtf8Bytes(payload, SnakeCase));
        var signature = SignatureAlgorithm.Ed25519.Sign(
            signingKey, Encoding.UTF8.GetBytes($"{header}.{body}"));
        return $"{header}.{body}.{B64Url(signature)}";
    }

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // --- happy path ---

    [Fact]
    public async Task ValidReceipt_IsVerified_WithFullWeight()
    {
        var payload = MakePayload();
        var jwt = BuildJwt(payload, _serviceKey);

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.Valid);
        result.IsVerified.Should().BeTrue();
        result.Weight.Should().Be(1.0);
        result.Payload!.Nonce.Should().Be(payload.Nonce);
        result.Payload.ServiceDid.Should().Be(ServiceDid);
    }

    // --- signature ---

    [Fact]
    public async Task ReceiptSignedByWrongKey_IsInvalidSignature()
    {
        using var otherKey = Key.Create(SignatureAlgorithm.Ed25519);
        var jwt = BuildJwt(MakePayload(), otherKey);

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.InvalidSignature);
        result.Weight.Should().Be(0.3);
    }

    [Fact]
    public async Task TamperedPayload_IsInvalidSignature()
    {
        var jwt = BuildJwt(MakePayload(), _serviceKey);
        var parts = jwt.Split('.');
        // Re-encode a different payload under the original signature.
        var forgedBody = B64Url(JsonSerializer.SerializeToUtf8Bytes(
            MakePayload(nonce: "forged-nonce"), SnakeCase));
        var forged = $"{parts[0]}.{forgedBody}.{parts[2]}";

        var result = await CreateVerifier().VerifyAsync(forged, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.InvalidSignature);
    }

    // --- structure ---

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    public async Task MalformedJwt_IsRejectedAsMalformed(string jwt)
    {
        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.MalformedJwt);
    }

    [Fact]
    public async Task NonJsonPayload_IsRejectedAsMalformed()
    {
        var header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"EdDSA"}"""));
        var body = B64Url(Encoding.UTF8.GetBytes("this is not json"));
        var jwt = $"{header}.{body}.{B64Url(new byte[64])}";

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.MalformedJwt);
    }

    // --- timestamp window ---

    [Fact]
    public async Task ExpiredReceipt_IsRejected_BeforeSignatureCheck()
    {
        var jwt = BuildJwt(MakePayload(timestamp: DateTimeOffset.UtcNow.AddMinutes(-6)), _serviceKey);

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.TimestampExpired);
    }

    [Fact]
    public async Task FutureDatedReceipt_BeyondClockSkew_IsRejected()
    {
        var jwt = BuildJwt(MakePayload(timestamp: DateTimeOffset.UtcNow.AddMinutes(2)), _serviceKey);

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.TimestampExpired);
    }

    [Fact]
    public async Task ReceiptWithinClockSkew_IsAccepted()
    {
        var jwt = BuildJwt(MakePayload(timestamp: DateTimeOffset.UtcNow.AddSeconds(30)), _serviceKey);

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.Valid);
    }

    // --- coherence and binding ---

    [Fact]
    public async Task ServiceDidMismatch_IsRejected()
    {
        var jwt = BuildJwt(MakePayload(serviceDid: "did:web:other.example.com"), _serviceKey);

        var result = await CreateVerifier().VerifyAsync(jwt, ServiceDid, AgentDid);

        result.IsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ReceiptForAnotherAgent_IsRejected_WithoutBurningItsNonce()
    {
        // M2: a receipt is not a bearer token. Agent B replaying agent A's receipt must be
        // rejected AND must not consume A's nonce — A's own later submission must still verify.
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        var jwt = BuildJwt(MakePayload(agentDid: AgentDid), _serviceKey);

        var replayByB = await verifier.VerifyAsync(jwt, ServiceDid, "did:web:attacker.example.com");
        replayByB.IsVerified.Should().BeFalse();

        var legitimateByA = await verifier.VerifyAsync(jwt, ServiceDid, AgentDid);
        legitimateByA.Status.Should().Be(ReceiptVerificationStatus.Valid);
    }

    // --- nonce anti-replay ---

    [Fact]
    public async Task ReplayedReceipt_IsRejected_WithZeroWeight()
    {
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        var jwt = BuildJwt(MakePayload(), _serviceKey);

        (await verifier.VerifyAsync(jwt, ServiceDid, AgentDid)).IsVerified.Should().BeTrue();
        var replay = await verifier.VerifyAsync(jwt, ServiceDid, AgentDid);

        replay.Status.Should().Be(ReceiptVerificationStatus.NonceAlreadyUsed);
        replay.Weight.Should().Be(0.0);
    }

    [Fact]
    public async Task TransientDidResolutionFailure_DoesNotBurnTheNonce()
    {
        // The nonce is claimed only after the signature verifies: a transient resolution failure
        // must not consume it, so an honest re-submission of the same receipt still succeeds.
        var cache = new FakeCacheService();
        byte[]? resolved = null;
        var verifier = new ReceiptVerifier(
            new StubDidResolver(_ => resolved), cache, NullLogger<ReceiptVerifier>.Instance);
        var jwt = BuildJwt(MakePayload(), _serviceKey);

        var firstTry = await verifier.VerifyAsync(jwt, ServiceDid, AgentDid);
        firstTry.Status.Should().Be(ReceiptVerificationStatus.DidResolutionFailed);

        resolved = _serviceKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var retry = await verifier.VerifyAsync(jwt, ServiceDid, AgentDid);
        retry.Status.Should().Be(ReceiptVerificationStatus.Valid);
    }

    private sealed class StubDidResolver : IDidResolver
    {
        private readonly Func<string, byte[]?> _resolve;
        public StubDidResolver(Func<string, byte[]?> resolve) => _resolve = resolve;
        public Task<byte[]?> ResolvePublicKeyAsync(string did) => Task.FromResult(_resolve(did));
    }
}

/// <summary>
/// Full receipt chain with a REAL standard-conformant DID document: a fresh Ed25519 key is
/// multicodec-prefixed (0xED01), base58btc-encoded into a z6Mk… publicKeyMultibase, served as
/// did.json over a stubbed HTTP transport, resolved by the production DidWebResolver, and used to
/// verify a really-signed receipt. This is the exact path H1 broke (2026-07 audit): a service
/// following docs/receipts.md to the letter must get verified weight 1.0.
/// </summary>
public class ReceiptChainEndToEndTests
{
    private const string ServiceDid = "did:web:svc-e2e.example.com";
    private const string AgentDid = "did:web:agent-e2e.example.com";

    [Fact]
    public async Task StandardMultibaseDidDocument_YieldsVerifiedReceipt()
    {
        using var serviceKey = Key.Create(SignatureAlgorithm.Ed25519);
        var publicKey = serviceKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        // Ed25519VerificationKey2020: multibase = 'z' + base58btc(0xED 0x01 || key).
        var prefixed = new byte[] { 0xED, 0x01 }.Concat(publicKey).ToArray();
        var multibase = "z" + Base58Encoder.Encode(prefixed);
        multibase.Should().StartWith("z6Mk", "a multicodec-prefixed Ed25519 key encodes to the standard z6Mk… form");

        var didJson = $$"""
            {
              "@context": ["https://www.w3.org/ns/did/v1"],
              "id": "{{ServiceDid}}",
              "verificationMethod": [{
                "id": "{{ServiceDid}}#key-1",
                "type": "Ed25519VerificationKey2020",
                "controller": "{{ServiceDid}}",
                "publicKeyMultibase": "{{multibase}}"
              }]
            }
            """;

        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be("https://svc-e2e.example.com/.well-known/did.json");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(didJson, Encoding.UTF8, "application/json"),
            };
        });

        var resolver = new DidWebResolver(
            new StubHttpClientFactory(handler),
            new FakeCacheService(),
            NullLogger<DidWebResolver>.Instance);
        var verifier = new ReceiptVerifier(
            resolver, new FakeCacheService(), NullLogger<ReceiptVerifier>.Instance);

        var payload = new ReceiptPayload
        {
            ServiceDid = ServiceDid,
            AgentDid = AgentDid,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Nonce = Guid.NewGuid().ToString(),
            Endpoint = "/v1/echo",
            Method = "GET",
            StatusCode = 200,
        };
        var header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"EdDSA","typ":"JWT"}"""));
        var body = B64Url(JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }));
        var signature = SignatureAlgorithm.Ed25519.Sign(
            serviceKey, Encoding.UTF8.GetBytes($"{header}.{body}"));
        var jwt = $"{header}.{body}.{B64Url(signature)}";

        var result = await verifier.VerifyAsync(jwt, ServiceDid, AgentDid);

        result.Status.Should().Be(ReceiptVerificationStatus.Valid);
        result.Weight.Should().Be(1.0);
    }

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

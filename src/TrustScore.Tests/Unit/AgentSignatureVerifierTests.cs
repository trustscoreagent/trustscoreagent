using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using TrustScore.Api.Receipts;
using TrustScore.Core.Models;
using TrustScore.Tests.Integration;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// Tests AgentSignatureVerifier against REAL Ed25519 signatures over the canonical payload.
/// This is what turns X-Agent-DID from a self-asserted string into proof of key possession, so
/// every way a forged or replayed signature could slip through is pinned here.
/// </summary>
public class AgentSignatureVerifierTests
{
    private const string Method = "POST";
    private const string Path = "/v1/rate";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"service":"api.example.com"}""");

    private readonly Key _agentKey = Key.Create(SignatureAlgorithm.Ed25519);

    private string AgentDid => DidKeyFor(_agentKey);

    private static AgentSignatureVerifier CreateVerifier(FakeCacheService? cache = null)
        => new(cache ?? new FakeCacheService(), NullLogger<AgentSignatureVerifier>.Instance);

    /// <summary>Builds the did:key for a keypair: base58btc of 0xED01 + the raw public key.</summary>
    private static string DidKeyFor(Key key)
    {
        var raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return "did:key:z" + Base58Encode(new byte[] { 0xED, 0x01 }.Concat(raw).ToArray());
    }

    private AgentSignatureHeaders Sign(
        Key? signingKey = null,
        string? agentDid = null,
        DateTimeOffset? timestamp = null,
        string? nonce = null,
        byte[]? body = null,
        string method = Method,
        string path = Path)
    {
        var did = agentDid ?? AgentDid;
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToString("o");
        var n = nonce ?? Guid.NewGuid().ToString("N");

        var payload = AgentSignaturePayload.CanonicalBytes(method, path, did, ts, n, body ?? Body);
        var signature = SignatureAlgorithm.Ed25519.Sign(signingKey ?? _agentKey, payload);

        return new AgentSignatureHeaders(did, B64Url(signature), ts, n);
    }

    // --- happy path ---

    [Fact]
    public async Task ValidSignature_IsVerified()
    {
        var result = await CreateVerifier().VerifyAsync(Sign(), Method, Path, Body);

        result.IsVerified.Should().BeTrue();
        result.Status.Should().Be(AgentSignatureStatus.Valid);
    }

    // --- unsigned vs. malformed: the distinction that must not blur ---

    [Fact]
    public async Task NoSignatureHeaders_IsMissing_NotRejected()
    {
        // A legacy unsigned client must keep working, at whatever weight the endpoint decides.
        var headers = new AgentSignatureHeaders("did:key:zSomething", null, null, null);

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Missing);
        result.IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task PartiallySignedRequest_IsRejected_NotDowngradedToUnsigned()
    {
        // Otherwise dropping one header would be a way back into the lenient unsigned path.
        var signed = Sign();
        var headers = signed with { Nonce = null };

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
        result.IsRejected.Should().BeTrue();
    }

    // --- forgery ---

    [Fact]
    public async Task SignatureFromAnotherKey_IsInvalid()
    {
        using var attackerKey = Key.Create(SignatureAlgorithm.Ed25519);
        // Attacker signs correctly, but claims the victim's DID.
        var headers = Sign(signingKey: attackerKey, agentDid: AgentDid);

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Fact]
    public async Task TamperedBody_IsInvalid()
    {
        var headers = Sign(body: Body);
        var tampered = Encoding.UTF8.GetBytes("""{"service":"evil.example.com"}""");

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, tampered);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Theory]
    [InlineData("GET", Path)]
    [InlineData(Method, "/v1/admin/eigentrust")]
    public async Task SignatureIsBoundToMethodAndPath(string method, string path)
    {
        // A signature captured for POST /v1/rate must not authorise a different request.
        var headers = Sign(method: Method, path: Path);

        var result = await CreateVerifier().VerifyAsync(headers, method, path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Fact]
    public async Task TamperedTimestamp_IsInvalid()
    {
        // Shifting the timestamp to extend freshness breaks the signature it was signed under.
        var headers = Sign() with { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o") };

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    // --- freshness ---

    [Fact]
    public async Task ExpiredTimestamp_IsRejected()
    {
        var headers = Sign(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.TimestampExpired);
    }

    [Fact]
    public async Task FutureTimestamp_IsRejected()
    {
        // A future-dated signature would stay valid long after its nonce entry expired.
        var headers = Sign(timestamp: DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.TimestampExpired);
    }

    [Fact]
    public async Task SmallClockSkew_IsTolerated()
    {
        var headers = Sign(timestamp: DateTimeOffset.UtcNow.AddSeconds(30));

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.IsVerified.Should().BeTrue();
    }

    // --- replay ---

    [Fact]
    public async Task ReplayedNonce_IsRejected_OnSecondUse()
    {
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        var headers = Sign(nonce: "fixed-nonce-value");

        (await verifier.VerifyAsync(headers, Method, Path, Body)).IsVerified.Should().BeTrue();
        var replay = await verifier.VerifyAsync(headers, Method, Path, Body);

        replay.Status.Should().Be(AgentSignatureStatus.NonceAlreadyUsed);
    }

    [Fact]
    public async Task NonceIsScopedPerAgent_SoOneAgentCannotBurnAnothers()
    {
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        using var otherKey = Key.Create(SignatureAlgorithm.Ed25519);

        var mine = Sign(nonce: "shared-nonce-value");
        var theirs = Sign(signingKey: otherKey, agentDid: DidKeyFor(otherKey), nonce: "shared-nonce-value");

        (await verifier.VerifyAsync(mine, Method, Path, Body)).IsVerified.Should().BeTrue();
        (await verifier.VerifyAsync(theirs, Method, Path, Body)).IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task FailedSignature_DoesNotBurnTheNonce()
    {
        // A transient failure must not lock out the honest retry that follows.
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        using var attackerKey = Key.Create(SignatureAlgorithm.Ed25519);

        var forged = Sign(signingKey: attackerKey, nonce: "reusable-nonce");
        (await verifier.VerifyAsync(forged, Method, Path, Body)).IsVerified.Should().BeFalse();

        var honest = Sign(nonce: "reusable-nonce");
        (await verifier.VerifyAsync(honest, Method, Path, Body)).IsVerified.Should().BeTrue();
    }

    // --- malformed input ---

    [Fact]
    public async Task UnresolvableDid_IsRejected()
    {
        var headers = Sign(agentDid: "did:web:agent.example.com");

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.UnresolvableDid);
    }

    [Fact]
    public async Task NonBase64Signature_IsMalformed_NotInvalid()
    {
        var headers = Sign() with { Signature = "not!base64!" };

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
    }

    [Fact]
    public async Task UnparseableTimestamp_IsMalformed()
    {
        var headers = Sign() with { Timestamp = "not-a-date" };

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
    }

    [Theory]
    [InlineData("short")]                                   // below the nonce floor
    [InlineData("nonce-that-is-far-too-long-to-be-accepted-and-keeps-going-well-past-the-limit-we-allow-for-a-single-header-value")]
    public async Task OutOfBoundsNonce_IsMalformed(string nonce)
    {
        var headers = Sign() with { Nonce = nonce };

        var result = await CreateVerifier().VerifyAsync(headers, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
    }

    // --- canonical payload ---

    [Fact]
    public void CanonicalPayload_HasTheDocumentedShape()
    {
        // Every client must reproduce this byte for byte, so pin the exact layout.
        var canonical = AgentSignaturePayload.Canonicalize(
            "post", "/v1/rate", "did:key:zAbc", "2026-08-29T10:00:00.0000000+00:00", "nonce123", Body);

        var lines = canonical.Split('\n');

        lines.Should().HaveCount(7);
        lines[0].Should().Be("trustscore-v1");
        lines[1].Should().Be("POST");           // method is upper-cased
        lines[2].Should().Be("/v1/rate");
        lines[3].Should().Be("did:key:zAbc");
        lines[4].Should().Be("2026-08-29T10:00:00.0000000+00:00");
        lines[5].Should().Be("nonce123");
        lines[6].Should().MatchRegex("^[0-9a-f]{64}$");  // sha256 of the body, lowercase hex
    }

    [Fact]
    public void CanonicalPayload_BindsTheExactBodyBytes()
    {
        string Hash(byte[] body) => AgentSignaturePayload
            .Canonicalize(Method, Path, "did:key:zAbc", "2026-08-29T10:00:00Z", "nonce123", body)
            .Split('\n')[6];

        Hash(Encoding.UTF8.GetBytes("{}")).Should().NotBe(Hash(Encoding.UTF8.GetBytes("{ }")));
        Hash(Array.Empty<byte>()).Should()
            .Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base58Encode(byte[] data)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = new System.Numerics.BigInteger(data, isUnsigned: true, isBigEndian: true);
        var sb = new StringBuilder();
        while (value > 0)
        {
            value = System.Numerics.BigInteger.DivRem(value, 58, out var remainder);
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

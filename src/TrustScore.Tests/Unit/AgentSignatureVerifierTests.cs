using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using TrustScore.Api.Receipts;
using TrustScore.Core.Models;
using TrustScore.Tests.Integration;
using TrustScore.Tests.TestSupport;
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
    private const string Audience = AgentSigner.TestAudience;
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"service":"api.example.com"}""");

    private readonly Key _agentKey = Key.Create(SignatureAlgorithm.Ed25519);

    private string AgentDid => DidKeyFor(_agentKey);

    private static AgentSignatureVerifier CreateVerifier(
        FakeCacheService? cache = null, string? configuredAudience = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AgentSignatureVerifier.AudienceConfigKey] = configuredAudience,
            })
            .Build();

        return new AgentSignatureVerifier(
            cache ?? new FakeCacheService(), config, NullLogger<AgentSignatureVerifier>.Instance);
    }

    private static string DidKeyFor(Key key) => AgentSigner.DidKeyFor(key);

    private AgentSignatureHeaders Sign(
        Key? signingKey = null,
        string? agentDid = null,
        DateTimeOffset? timestamp = null,
        string? nonce = null,
        byte[]? body = null,
        string method = Method,
        string path = Path,
        string audience = Audience)
        => AgentSigner.Sign(
            signingKey ?? _agentKey,
            body ?? Body,
            method,
            path,
            agentDid ?? AgentDid,
            timestamp,
            nonce,
            audience);

    // --- happy path ---

    [Fact]
    public async Task ValidSignature_IsVerified()
    {
        var result = await CreateVerifier().VerifyAsync(Sign(), Audience, Method, Path, Body);

        result.IsVerified.Should().BeTrue();
        result.Status.Should().Be(AgentSignatureStatus.Valid);
    }

    // --- unsigned vs. malformed: the distinction that must not blur ---

    [Fact]
    public async Task NoSignatureHeaders_IsMissing_NotRejected()
    {
        // A legacy unsigned client must keep working, at whatever weight the endpoint decides.
        var headers = new AgentSignatureHeaders("did:key:zSomething", null, null, null);

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Missing);
        result.IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task PartiallySignedRequest_IsRejected_NotDowngradedToUnsigned()
    {
        // Otherwise dropping one header would be a way back into the lenient unsigned path.
        var signed = Sign();
        var headers = signed with { Nonce = null };

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

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

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Fact]
    public async Task TamperedBody_IsInvalid()
    {
        var headers = Sign(body: Body);
        var tampered = Encoding.UTF8.GetBytes("""{"service":"evil.example.com"}""");

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, tampered);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Theory]
    [InlineData("GET", Path)]
    [InlineData(Method, "/v1/admin/eigentrust")]
    public async Task SignatureIsBoundToMethodAndPath(string method, string path)
    {
        // A signature captured for POST /v1/rate must not authorise a different request.
        var headers = Sign(method: Method, path: Path);

        var result = await CreateVerifier().VerifyAsync(headers, Audience, method, path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Fact]
    public async Task TamperedTimestamp_IsInvalid()
    {
        // Shifting the timestamp to extend freshness breaks the signature it was signed under.
        var headers = Sign() with { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o") };

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    // --- freshness ---

    [Fact]
    public async Task ExpiredTimestamp_IsRejected()
    {
        var headers = Sign(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.TimestampExpired);
    }

    [Fact]
    public async Task FutureTimestamp_IsRejected()
    {
        // A future-dated signature would stay valid long after its nonce entry expired.
        var headers = Sign(timestamp: DateTimeOffset.UtcNow.AddMinutes(10));

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.TimestampExpired);
    }

    [Fact]
    public async Task SmallClockSkew_IsTolerated()
    {
        var headers = Sign(timestamp: DateTimeOffset.UtcNow.AddSeconds(30));

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.IsVerified.Should().BeTrue();
    }

    // --- replay ---

    [Fact]
    public async Task ReplayedNonce_IsRejected_OnSecondUse()
    {
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        var headers = Sign(nonce: "fixed-nonce-value");

        (await verifier.VerifyAsync(headers, Audience, Method, Path, Body)).IsVerified.Should().BeTrue();
        var replay = await verifier.VerifyAsync(headers, Audience, Method, Path, Body);

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

        (await verifier.VerifyAsync(mine, Audience, Method, Path, Body)).IsVerified.Should().BeTrue();
        (await verifier.VerifyAsync(theirs, Audience, Method, Path, Body)).IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task FailedSignature_DoesNotBurnTheNonce()
    {
        // A transient failure must not lock out the honest retry that follows.
        var cache = new FakeCacheService();
        var verifier = CreateVerifier(cache);
        using var attackerKey = Key.Create(SignatureAlgorithm.Ed25519);

        var forged = Sign(signingKey: attackerKey, nonce: "reusable-nonce");
        (await verifier.VerifyAsync(forged, Audience, Method, Path, Body)).IsVerified.Should().BeFalse();

        var honest = Sign(nonce: "reusable-nonce");
        (await verifier.VerifyAsync(honest, Audience, Method, Path, Body)).IsVerified.Should().BeTrue();
    }

    // --- malformed input ---

    [Fact]
    public async Task UnresolvableDid_IsRejected()
    {
        var headers = Sign(agentDid: "did:web:agent.example.com");

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.UnresolvableDid);
    }

    [Fact]
    public async Task NonBase64Signature_IsMalformed_NotInvalid()
    {
        var headers = Sign() with { Signature = "not!base64!" };

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
    }

    [Fact]
    public async Task UnparseableTimestamp_IsMalformed()
    {
        var headers = Sign() with { Timestamp = "not-a-date" };

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
    }

    [Theory]
    [InlineData("short")]                                   // below the nonce floor
    [InlineData("nonce-that-is-far-too-long-to-be-accepted-and-keeps-going-well-past-the-limit-we-allow-for-a-single-header-value")]
    public async Task OutOfBoundsNonce_IsMalformed(string nonce)
    {
        var headers = Sign() with { Nonce = nonce };

        var result = await CreateVerifier().VerifyAsync(headers, Audience, Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.Malformed);
    }

    // --- audience: cross-registry replay ---

    [Fact]
    public async Task SignatureAddressedToAnotherRegistry_DoesNotVerifyHere()
    {
        // The registry is self-hostable, so a hostile operator sees every signature its users
        // produce. Relaying one to the public registry must not forge a rating in their name.
        var verifier = CreateVerifier(configuredAudience: "api.trustscoreagent.com");
        var signedForEvil = Sign(audience: "evil-registry.example.com");

        var result = await verifier.VerifyAsync(
            signedForEvil, "api.trustscoreagent.com", Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Fact]
    public async Task ConfiguredAudience_IgnoresTheHostHeader()
    {
        // The relaying attacker controls the Host header, so the audience must come from this
        // server's own configuration, not from the request.
        var verifier = CreateVerifier(configuredAudience: "api.trustscoreagent.com");
        var signedForEvil = Sign(audience: "evil-registry.example.com");

        var result = await verifier.VerifyAsync(
            signedForEvil, "evil-registry.example.com", Method, Path, Body);

        result.Status.Should().Be(AgentSignatureStatus.InvalidSignature);
    }

    [Fact]
    public async Task ConfiguredAudience_AcceptsASignatureAddressedToThisRegistry()
    {
        var verifier = CreateVerifier(configuredAudience: "api.trustscoreagent.com");
        var headers = Sign(audience: "api.trustscoreagent.com");

        var result = await verifier.VerifyAsync(headers, "anything.example.com", Method, Path, Body);

        result.IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task AudienceMatching_IsCaseInsensitive()
    {
        // Host names are case-insensitive; a caller spelling the registry differently is not an
        // attacker and must not get an opaque 401.
        var verifier = CreateVerifier(configuredAudience: "API.TrustScoreAgent.com");
        var headers = Sign(audience: "api.trustscoreagent.com");

        var result = await verifier.VerifyAsync(headers, Audience, Method, Path, Body);

        result.IsVerified.Should().BeTrue();
    }

    // --- canonical payload ---

    [Fact]
    public void CanonicalPayload_HasTheDocumentedShape()
    {
        // Every client must reproduce this byte for byte, so pin the exact layout.
        var canonical = AgentSignaturePayload.Canonicalize(
            Audience, "post", "/v1/rate", "did:key:zAbc", "2026-08-29T10:00:00.0000000+00:00", "nonce123", Body);

        var lines = canonical.Split('\n');

        lines.Should().HaveCount(8);
        lines[0].Should().Be("trustscore-v1");
        lines[1].Should().Be(Audience);
        lines[2].Should().Be("POST");           // method is upper-cased
        lines[3].Should().Be("/v1/rate");
        lines[4].Should().Be("did:key:zAbc");
        lines[5].Should().Be("2026-08-29T10:00:00.0000000+00:00");
        lines[6].Should().Be("nonce123");
        lines[7].Should().MatchRegex("^[0-9a-f]{64}$");  // sha256 of the body, lowercase hex
    }

    [Fact]
    public void CanonicalPayload_LowercasesTheAudience()
    {
        var canonical = AgentSignaturePayload.Canonicalize(
            "API.TrustScoreAgent.COM", Method, Path, "did:key:zAbc", "2026-08-29T10:00:00Z", "nonce123", Body);

        canonical.Split('\n')[1].Should().Be("api.trustscoreagent.com");
    }

    [Fact]
    public void CanonicalPayload_BindsTheExactBodyBytes()
    {
        string Hash(byte[] body) => AgentSignaturePayload
            .Canonicalize(Audience, Method, Path, "did:key:zAbc", "2026-08-29T10:00:00Z", "nonce123", body)
            .Split('\n')[7];

        Hash(Encoding.UTF8.GetBytes("{}")).Should().NotBe(Hash(Encoding.UTF8.GetBytes("{ }")));
        Hash(Array.Empty<byte>()).Should()
            .Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

}

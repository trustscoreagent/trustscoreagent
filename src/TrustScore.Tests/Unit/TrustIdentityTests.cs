using FluentAssertions;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// An agent's reputation must only be movable by ratings it actually made. Without the split these
/// tests pin, anyone could submit deliberately inconsistent ratings under a victim's DID (a plain
/// header on an unsigned request) and drive that victim's EigenTrust score down, which shrinks the
/// weight of the victim's own honest ratings.
/// </summary>
public class TrustIdentityTests
{
    private readonly EigenTrustEngine _engine = new();

    private const string Victim = "did:key:zVictim";

    private static AgentRatingRecord Signed(string agent, string service, int status, int latency) =>
        new(agent, service, status, latency, SchemaValid: true, ReceiptVerified: false, SignatureVerified: true);

    private static AgentRatingRecord Unsigned(string agent, string service, int status, int latency) =>
        new(agent, service, status, latency, SchemaValid: true, ReceiptVerified: false, SignatureVerified: false);

    [Fact]
    public void For_NamespacesOnlyUnsignedRatings()
    {
        TrustIdentity.For(Victim, signatureVerified: true).Should().Be(Victim);
        TrustIdentity.For(Victim, signatureVerified: false).Should().Be("unsigned:" + Victim);
    }

    [Fact]
    public void UnsignedRatings_AccrueUnderASeparateIdentity()
    {
        var ratings = new List<AgentRatingRecord>
        {
            Signed(Victim, "service-1", 200, 100),
            Unsigned(Victim, "service-1", 500, 9000),
        };

        var result = _engine.ComputeTrustScores(ratings);

        result.Should().ContainKey(Victim);
        result.Should().ContainKey("unsigned:" + Victim);
    }

    [Fact]
    public void NamingTheVictim_GivesTheAttackerNoAdvantage()
    {
        // EigenTrust is global, so adding any noisy rater shifts the whole vector a little. The
        // property that matters is narrower and is the one the attack relied on: pointing the flood
        // at a specific victim must be worth no more than pointing it anywhere else.
        var honest = new List<AgentRatingRecord>
        {
            Signed(Victim, "service-1", 200, 100),
            Signed(Victim, "service-2", 200, 120),
            Signed("did:key:zPeer", "service-1", 200, 100),
            Signed("did:key:zPeer", "service-2", 200, 120),
        };

        List<AgentRatingRecord> FloodUnder(string claimedDid)
        {
            var all = new List<AgentRatingRecord>(honest);
            for (var i = 0; i < 20; i++)
                all.Add(Unsigned(claimedDid, i % 2 == 0 ? "service-1" : "service-2", 500, 30000));
            return all;
        }

        var targeted = _engine.ComputeTrustScores(FloodUnder(Victim))[Victim];
        var untargeted = _engine.ComputeTrustScores(FloodUnder("did:key:zNobody"))[Victim];

        targeted.Should().Be(untargeted,
            "impersonating a DID must not damage it any more than flooding under an unrelated one");
    }

    [Fact]
    public void SignedImpersonationAttempts_StillDamageTheRealIdentity_BecauseTheyAreReal()
    {
        // The converse guard: the split must not become a way to dodge accountability. A rating the
        // agent genuinely signed counts against it, as it should.
        // Enough honest raters that the added ratings cannot flip the majority consensus, which
        // would otherwise make the outlier look correct and raise its score instead.
        var honest = new List<AgentRatingRecord> { Signed(Victim, "service-1", 200, 100) };
        for (var i = 0; i < 8; i++)
            honest.Add(Signed($"did:key:zPeer{i}", "service-1", 200, 100));

        var before = _engine.ComputeTrustScores(honest)[Victim];

        var inconsistent = new List<AgentRatingRecord>(honest)
        {
            Signed(Victim, "service-1", 500, 30000),
            Signed(Victim, "service-1", 500, 30000),
        };

        var after = _engine.ComputeTrustScores(inconsistent)[Victim];

        after.Should().BeLessThan(before);
    }

    [Fact]
    public void UnsignedRatings_StillFeedServiceConsensus()
    {
        // They are namespaced for identity, not discarded: almost all current data is unsigned, so
        // dropping it from consensus would leave the engine with nothing to measure agents against.
        var ratings = new List<AgentRatingRecord>
        {
            Unsigned("did:key:zA", "service-1", 200, 100),
            Unsigned("did:key:zB", "service-1", 200, 105),
            Unsigned("did:key:zC", "service-1", 500, 30000),
        };

        var result = _engine.ComputeTrustScores(ratings);

        // The outlier disagreeing with two consistent raters must score below them.
        result["unsigned:did:key:zC"].Should().BeLessThan(result["unsigned:did:key:zA"]);
    }

    [Fact]
    public void AnAgentCannotForgeTheNamespacedFormOfAnotherIdentity()
    {
        // The prefix is only reachable through TrustIdentity, so a caller sending
        // X-Agent-DID: unsigned:did:key:zVictim signs (or asserts) a different string entirely.
        var spoofed = TrustIdentity.For("unsigned:" + Victim, signatureVerified: false);

        spoofed.Should().Be("unsigned:unsigned:" + Victim);
        spoofed.Should().NotBe(TrustIdentity.For(Victim, signatureVerified: false));
    }
}

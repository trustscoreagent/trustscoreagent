using FluentAssertions;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

public class EigenTrustTests
{
    private const string ProbeDid = "did:web:trustscoreagent.com:probe";
    private static readonly string ProbeIdentity = TrustIdentity.For(ProbeDid, signatureVerified: false);

    private readonly EigenTrustEngine _engine = new(new[] { ProbeIdentity });

    // The probe rates unsigned, as in production.
    private static AgentRatingRecord Probe(string service, int status = 200, int latency = 100, bool? schema = true) =>
        new(ProbeDid, service, status, latency, schema, ReceiptVerified: false, SignatureVerified: false);

    private static AgentRatingRecord Agent(string did, string service, int status = 200, int latency = 100,
        bool? schema = true, bool receipt = false) =>
        new(did, service, status, latency, schema, receipt, SignatureVerified: true);

    private static List<AgentRatingRecord> ProbeOn(int services) =>
        Enumerable.Range(0, services).Select(i => Probe($"service-{i}")).ToList();

    [Fact]
    public void EmptyRatings_ReturnsEmpty()
    {
        _engine.ComputeTrustScores(new List<AgentRatingRecord>()).Should().BeEmpty();
    }

    [Fact]
    public void AnAgentNeverCompared_GetsTheDefault()
    {
        var result = _engine.ComputeTrustScores(new List<AgentRatingRecord> { Agent("agent-a", "service-1") });

        result["agent-a"].Should().Be(EigenTrustEngine.DefaultTrustScore);
    }

    [Fact]
    public void TheProbe_IsPreTrusted()
    {
        var result = _engine.ComputeTrustScores(ProbeOn(3));

        result[ProbeIdentity].Should().Be(EigenTrustEngine.PreTrustedScore);
    }

    [Fact]
    public void AgreeingWithTheProbe_EarnsTrust_InProportionToExperience()
    {
        var ratings = ProbeOn(EigenTrustEngine.ExperienceServices);
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
            ratings.Add(Agent("veteran", $"service-{i}"));
        for (var i = 0; i < 3; i++)
            ratings.Add(Agent("newcomer", $"service-{i}"));

        var result = _engine.ComputeTrustScores(ratings);

        result["veteran"].Should().Be(EigenTrustEngine.MaxEarnedTrust);
        result["newcomer"].Should().BeInRange(EigenTrustEngine.DefaultTrustScore + 0.01, result["veteran"] - 0.01);
    }

    [Fact]
    public void ContradictingTheProbe_DrivesTrustToTheFloor()
    {
        var ratings = ProbeOn(EigenTrustEngine.ExperienceServices);
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
            ratings.Add(Agent("liar", $"service-{i}", status: 500, latency: 9000, schema: false));

        _engine.ComputeTrustScores(ratings)["liar"].Should().Be(EigenTrustEngine.MinTrustScore);
    }

    [Fact]
    public void ReportingAFailedPaidCall_IsNotContradictingTheProbesChallenge()
    {
        // The probe sees the unpaid 402 challenge of an x402 endpoint. An agent that paid and got a
        // 500 reports something the probe cannot see: that is the signal the registry exists for,
        // and it must not cost the agent its trust.
        var ratings = new List<AgentRatingRecord>();
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
        {
            ratings.Add(Probe($"paid-{i}", status: 402));
            ratings.Add(Agent("payer", $"paid-{i}", status: 500, latency: 300, schema: null));
        }

        _engine.ComputeTrustScores(ratings)["payer"].Should().Be(EigenTrustEngine.DefaultTrustScore,
            "a delivery report is never compared with a challenge observation");
    }

    [Fact]
    public void AChallengeReport_IsComparedWithTheProbesChallenge()
    {
        var ratings = new List<AgentRatingRecord>();
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
        {
            ratings.Add(Probe($"paid-{i}", status: 402));
            ratings.Add(Agent("observer", $"paid-{i}", status: 402));
        }

        _engine.ComputeTrustScores(ratings)["observer"].Should().Be(EigenTrustEngine.MaxEarnedTrust);
    }

    [Fact]
    public void AnIsolatedClique_EarnsNothing()
    {
        // Ten identities agreeing with each other on services nobody trusted observed: with no
        // anchor and no earned trust among them, nothing they say can be compared, so they stay at
        // the default instead of certifying each other.
        var ratings = ProbeOn(3);
        for (var s = 0; s < 10; s++)
            for (var i = 0; i < 10; i++)
                ratings.Add(Agent($"sybil-{s}", $"clique-{i}", latency: 50));

        var result = _engine.ComputeTrustScores(ratings);

        for (var s = 0; s < 10; s++)
            result[$"sybil-{s}"].Should().Be(EigenTrustEngine.DefaultTrustScore);
    }

    [Fact]
    public void AFloodOfNewIdentities_CannotOverturnTrustedAgents()
    {
        // Two agents earned trust against the probe, then both rate an endpoint the probe never
        // saw. A hundred fresh identities claiming the opposite carry no vote, so the trusted agents
        // stay coherent and the flood earns nothing.
        var ratings = ProbeOn(EigenTrustEngine.ExperienceServices);
        foreach (var honest in new[] { "honest-1", "honest-2" })
        {
            for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
                ratings.Add(Agent(honest, $"service-{i}"));
            ratings.Add(Agent(honest, "unprobed"));
        }
        for (var s = 0; s < 100; s++)
            ratings.Add(Agent($"flood-{s}", "unprobed", status: 500, latency: 9000, schema: false));

        var result = _engine.ComputeTrustScores(ratings);

        result["honest-1"].Should().Be(EigenTrustEngine.MaxEarnedTrust);
        result["honest-2"].Should().Be(EigenTrustEngine.MaxEarnedTrust);
        result["flood-0"].Should().BeLessThan(EigenTrustEngine.DefaultTrustScore,
            "contradicting agents that earned trust is a measured disagreement");
    }

    [Fact]
    public void TrustPropagates_ThroughAgentsThatEarnedIt()
    {
        // Transitivity: an agent the probe can vouch for extends the consensus to endpoints the
        // probe never measured, and a newcomer agreeing there earns trust too.
        var ratings = ProbeOn(EigenTrustEngine.ExperienceServices);
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
        {
            ratings.Add(Agent("bridge", $"service-{i}"));
            ratings.Add(Agent("bridge", $"unprobed-{i}"));
            ratings.Add(Agent("follower", $"unprobed-{i}"));
        }

        var result = _engine.ComputeTrustScores(ratings);

        result["follower"].Should().BeGreaterThan(EigenTrustEngine.DefaultTrustScore + 0.3);
    }

    [Fact]
    public void ASelfIssuedReceipt_DoesNotMakeAnAnchor()
    {
        // Audit C1: a service signing its own receipts made its operator the only seed, and
        // everyone else's trust collapsed. Receipts now play no part in trust.
        var ratings = ProbeOn(EigenTrustEngine.ExperienceServices);
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
            ratings.Add(Agent("honest", $"service-{i}"));
        ratings.Add(Agent("operator", "evil.com", receipt: true));

        var result = _engine.ComputeTrustScores(ratings);

        result["operator"].Should().Be(EigenTrustEngine.DefaultTrustScore);
        result["honest"].Should().Be(EigenTrustEngine.MaxEarnedTrust);
        result[ProbeIdentity].Should().Be(EigenTrustEngine.PreTrustedScore);
    }

    [Fact]
    public void RepeatingAnOpinion_IsStillOneVote()
    {
        var ratings = ProbeOn(1);
        for (var r = 0; r < 50; r++)
            ratings.Add(Agent("loud", "service-0"));
        ratings.Add(Agent("quiet", "service-0"));

        var result = _engine.ComputeTrustScores(ratings);

        result["loud"].Should().Be(result["quiet"]);
    }

    [Fact]
    public void LatencyIsJudgedByTheThresholdTheScoreUses()
    {
        // 150 ms against the probe's 40 ms is still "fast": an agent far from the service must not
        // look incoherent for being farther away.
        var ratings = new List<AgentRatingRecord>();
        for (var i = 0; i < EigenTrustEngine.ExperienceServices; i++)
        {
            ratings.Add(Probe($"service-{i}", latency: 40));
            ratings.Add(Agent("far-away", $"service-{i}", latency: 150));
        }

        _engine.ComputeTrustScores(ratings)["far-away"].Should().Be(EigenTrustEngine.MaxEarnedTrust);
    }

    [Fact]
    public void WithoutAnyAnchor_EveryoneStaysAtTheDefault()
    {
        var engine = new EigenTrustEngine();
        var ratings = new List<AgentRatingRecord>
        {
            Agent("agent-a", "service-1"),
            Agent("agent-b", "service-1"),
            Agent("agent-c", "service-1", status: 500),
        };

        engine.ComputeTrustScores(ratings).Values.Should().AllSatisfy(v => v.Should().Be(EigenTrustEngine.DefaultTrustScore));
    }

    [Fact]
    public void AllScores_AreWithinBounds()
    {
        var ratings = ProbeOn(5);
        for (var a = 0; a < 20; a++)
            for (var s = 0; s < 5; s++)
                ratings.Add(Agent($"agent-{a}", $"service-{s}", status: a % 3 == 0 ? 500 : 200, latency: 100 + a * 200));

        foreach (var (_, score) in _engine.ComputeTrustScores(ratings))
        {
            double.IsNaN(score).Should().BeFalse();
            score.Should().BeInRange(EigenTrustEngine.MinTrustScore, EigenTrustEngine.PreTrustedScore);
        }
    }

    [Fact]
    public void ManyAgents_Converges()
    {
        var ratings = ProbeOn(50);
        for (var a = 0; a < 100; a++)
            for (var s = 0; s < 50; s++)
                ratings.Add(Agent($"agent-{a}", $"service-{s}", latency: 100 + a));

        _engine.ComputeTrustScores(ratings).Should().HaveCount(101);
    }

    [Fact]
    public void Earned_MovesFromTheDefaultTowardTheAgreementRate()
    {
        EigenTrustEngine.Earned(0, 0).Should().Be(EigenTrustEngine.DefaultTrustScore);
        EigenTrustEngine.Earned(EigenTrustEngine.ExperienceServices, EigenTrustEngine.ExperienceServices)
            .Should().Be(EigenTrustEngine.MaxEarnedTrust);
        EigenTrustEngine.Earned(EigenTrustEngine.ExperienceServices * 3, 0).Should().Be(EigenTrustEngine.MinTrustScore);
    }
}

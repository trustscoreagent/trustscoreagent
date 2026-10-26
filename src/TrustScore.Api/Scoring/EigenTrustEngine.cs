using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Scoring;

/// <summary>
/// Agent trust, in the spirit of EigenTrust (Kamvar, Schlosser, Garcia-Molina, 2003): trust is
/// anchored on pre-trusted peers and propagates to the agents whose observations agree with what
/// trusted agents observed. See docs/EIGENTRUST-SPEC.md for the design decisions.
///
/// <para>Each round, every (service, kind of observation) gets a consensus. Where a pre-trusted
/// peer (the registry's own probe) observed it, the consensus is that observation. Elsewhere it is
/// a vote of the other agents weighted by the trust they have earned above the default, so a crowd
/// of brand-new identities carries no vote at all. An agent's trust then follows how often it
/// agrees with that consensus and on how many services it was compared, and the rounds repeat
/// until the scores settle.</para>
///
/// <para>Only identity matters here, not receipts: a receipt is signed by the very service it is
/// about, so it proves a call happened but not that the rater is independent of that service.</para>
/// </summary>
public sealed class EigenTrustEngine
{
    /// <summary>Trust of an identity never compared yet. Below the 0.5 midpoint so that a fresh
    /// key (free to mint) weighs less than an agent that has shown it reports what others see.</summary>
    public const double DefaultTrustScore = 0.25;

    /// <summary>Floor: even an agent that disagrees with everything keeps a residual weight.</summary>
    public const double MinTrustScore = 0.1;

    /// <summary>The most an agent can earn. Below the pre-trusted peers, whose observation is the
    /// consensus where they have one.</summary>
    public const double MaxEarnedTrust = 0.9;

    /// <summary>Fixed trust of the pre-trusted peers.</summary>
    public const double PreTrustedScore = 1.0;

    /// <summary>Services an agent must have been compared on before its trust moves all the way to
    /// what its agreement rate earns. Fewer comparisons keep it proportionally closer to the default.</summary>
    internal const int ExperienceServices = 10;

    private const int MaxRounds = 20;
    private const double ConvergenceThreshold = 0.0001;
    private const int LatencyThresholdMs = 2000;

    // Agent DIDs are cheap to mint, so the number of agents is bounded to keep the job's memory and
    // time predictable. The excluded long tail (lowest rating counts) gets the default trust, like
    // any agent never compared. Pre-trusted peers are always kept.
    private const int MaxAgents = 5000;

    private readonly HashSet<string> _preTrusted;
    private readonly ILogger? _logger;

    /// <param name="preTrusted">Identities whose observations anchor the consensus, as
    /// <see cref="TrustIdentity.For"/> spells them (the probe rates unsigned, so
    /// <c>unsigned:did:web:trustscoreagent.com:probe</c>).</param>
    public EigenTrustEngine(IEnumerable<string>? preTrusted = null, ILogger? logger = null)
    {
        _preTrusted = new HashSet<string>(preTrusted ?? Array.Empty<string>(), StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>The kind of observation a rating is. A 402 is the unpaid payment challenge (what the
    /// probe measures on x402 endpoints); anything else is what came back from an actual call. The
    /// two are only ever compared with their own kind: an agent that paid and got a 500 is reporting
    /// something the probe could not see, not contradicting it.</summary>
    internal enum ObservationKind { Challenge, Delivery }

    internal static ObservationKind KindOf(int statusCode) =>
        statusCode == 402 ? ObservationKind.Challenge : ObservationKind.Delivery;

    public Dictionary<string, double> ComputeTrustScores(IReadOnlyList<AgentRatingRecord> ratings)
    {
        if (ratings.Count == 0)
            return new Dictionary<string, double>();

        // An unsigned rating names a DID the sender never proved it holds, so it accrues under a
        // separate identity; otherwise anyone could ruin or borrow a victim's standing.
        ratings = ratings
            .Select(r => r with { AgentDid = TrustIdentity.For(r.AgentDid, r.SignatureVerified) })
            .ToList();

        var distinctAgents = ratings.Select(r => r.AgentDid).Distinct().ToList();
        var scored = new HashSet<string>(distinctAgents, StringComparer.Ordinal);
        if (distinctAgents.Count > MaxAgents)
        {
            scored = ratings
                .GroupBy(r => r.AgentDid)
                .OrderByDescending(g => _preTrusted.Contains(g.Key))
                .ThenByDescending(g => g.Count())
                .Take(MaxAgents)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal);
            _logger?.LogWarning(
                "EigenTrust: {Total} distinct agents exceeds cap {Cap}; the rest get default trust",
                distinctAgents.Count, MaxAgents);
        }

        // One vote per agent per (service, kind): a rater that reported the same thing a thousand
        // times is still one opinion.
        var votes = ratings
            .Where(r => scored.Contains(r.AgentDid))
            .GroupBy(r => (r.AgentDid, r.ServiceDid, Kind: KindOf(r.StatusCode)))
            .Select(g => Vote.From(g.Key.AgentDid, (g.Key.ServiceDid, g.Key.Kind), g.ToList()))
            .ToList();
        var votesByTopic = votes.GroupBy(v => v.Topic).ToDictionary(g => g.Key, g => g.ToList());
        var votesByAgent = votes.GroupBy(v => v.Agent).ToDictionary(g => g.Key, g => g.ToList());

        var trust = scored.ToDictionary(
            a => a, a => _preTrusted.Contains(a) ? PreTrustedScore : DefaultTrustScore, StringComparer.Ordinal);

        for (var round = 0; round < MaxRounds; round++)
        {
            var consensus = votesByTopic.ToDictionary(kv => kv.Key, kv => Consensus.Of(kv.Value, trust, _preTrusted));

            var next = new Dictionary<string, double>(trust, StringComparer.Ordinal);
            var change = 0.0;
            foreach (var (agent, agentVotes) in votesByAgent)
            {
                if (_preTrusted.Contains(agent))
                    continue;

                var weight = VoteWeight(trust[agent]);
                int compared = 0, agreed = 0;
                foreach (var vote in agentVotes)
                {
                    var c = consensus[vote.Topic];
                    // Leave the agent's own vote out, so a service only it rated (or only it and
                    // untrusted identities rated) can never make it look coherent.
                    var verdict = c.Judge(vote, c.Anchored ? 0.0 : weight);
                    if (verdict is null)
                        continue;
                    compared++;
                    if (verdict == true) agreed++;
                }

                var updated = Earned(compared, agreed);
                change = Math.Max(change, Math.Abs(updated - trust[agent]));
                next[agent] = updated;
            }

            trust = next;
            if (change < ConvergenceThreshold)
                break;
        }

        var result = trust.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4), StringComparer.Ordinal);
        foreach (var a in distinctAgents)
            result.TryAdd(a, DefaultTrustScore);
        return result;
    }

    /// <summary>How much an agent's view counts in the consensus where no pre-trusted peer
    /// observed: only the trust it earned above the default, so new identities count for nothing.</summary>
    internal static double VoteWeight(double trust) => Math.Max(0.0, trust - DefaultTrustScore);

    /// <summary>Trust earned from being compared on <paramref name="compared"/> services and agreeing
    /// on <paramref name="agreed"/>: the agreement rate maps onto [Min, MaxEarned], and the fewer the
    /// comparisons the closer the result stays to the default.</summary>
    internal static double Earned(int compared, int agreed)
    {
        if (compared == 0)
            return DefaultTrustScore;
        var agreement = (double)agreed / compared;
        var target = MinTrustScore + (MaxEarnedTrust - MinTrustScore) * agreement;
        var experience = Math.Min(1.0, (double)compared / ExperienceServices);
        return DefaultTrustScore + (target - DefaultTrustScore) * experience;
    }

    /// <summary>What one agent reported about one (service, kind), reduced to the three facts the
    /// score is built from: did it work, was it under the latency threshold, was the schema valid.</summary>
    internal sealed record Vote(string Agent, (string Service, ObservationKind Kind) Topic, bool Success, bool Fast, bool? Schema)
    {
        public static Vote From(string agent, (string, ObservationKind) topic, List<AgentRatingRecord> records)
        {
            var success = records.Count(r => ResponseOutcome.IsSuccess(r.StatusCode, r.SchemaValid)) * 2 > records.Count;
            var fast = records.Count(r => r.LatencyMs <= LatencyThresholdMs) * 2 > records.Count;
            var withSchema = records.Where(r => r.SchemaValid.HasValue).ToList();
            bool? schema = withSchema.Count == 0 ? null : withSchema.Count(r => r.SchemaValid == true) * 2 > withSchema.Count;
            return new Vote(agent, topic, success, fast, schema);
        }
    }

    /// <summary>The consensus on one (service, kind), as weighted tallies so that any one voter can
    /// be left out of it cheaply.</summary>
    internal sealed class Consensus
    {
        public bool Anchored { get; private init; }
        private double _total, _success, _fast, _schemaTotal, _schemaValid;

        public static Consensus Of(List<Vote> votes, IReadOnlyDictionary<string, double> trust, IReadOnlySet<string> preTrusted)
        {
            var anchors = votes.Where(v => preTrusted.Contains(v.Agent)).ToList();
            var c = new Consensus { Anchored = anchors.Count > 0 };
            foreach (var v in c.Anchored ? anchors : votes)
                c.Add(v, c.Anchored ? 1.0 : VoteWeight(trust[v.Agent]));
            return c;
        }

        private void Add(Vote v, double w)
        {
            if (w <= 0) return;
            _total += w;
            if (v.Success) _success += w;
            if (v.Fast) _fast += w;
            if (v.Schema.HasValue)
            {
                _schemaTotal += w;
                if (v.Schema.Value) _schemaValid += w;
            }
        }

        /// <summary>Whether <paramref name="vote"/> agrees with the consensus once its own
        /// <paramref name="ownWeight"/> is taken out; null when nobody else carries any weight here.</summary>
        public bool? Judge(Vote vote, double ownWeight)
        {
            var total = _total - ownWeight;
            if (total <= 1e-9)
                return null;

            var success = (_success - (vote.Success ? ownWeight : 0)) * 2 > total;
            var fast = (_fast - (vote.Fast ? ownWeight : 0)) * 2 > total;
            if (vote.Success != success || vote.Fast != fast)
                return false;

            if (vote.Schema.HasValue)
            {
                var schemaTotal = _schemaTotal - ownWeight;
                if (schemaTotal > 1e-9)
                {
                    var valid = (_schemaValid - (vote.Schema.Value ? ownWeight : 0)) * 2 > schemaTotal;
                    if (vote.Schema.Value != valid)
                        return false;
                }
            }

            return true;
        }
    }
}

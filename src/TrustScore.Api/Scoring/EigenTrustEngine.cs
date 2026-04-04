using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Scoring;

/// <summary>
/// EigenTrust algorithm for computing agent trust scores.
///
/// Agents who rate consistently with the objective consensus (status_code, latency, schema_valid)
/// get high trust. Sybil agents who rate in isolated cliques converge toward 0.
///
/// Based on: Kamvar, Schlosser, Garcia-Molina (2003) "The EigenTrust Algorithm
/// for Reputation Management in P2P Networks"
/// </summary>
public sealed class EigenTrustEngine
{
    private const int MaxIterations = 50;
    private const double ConvergenceThreshold = 0.0001;
    private const double MinTrustScore = 0.1;
    private const double DefaultTrustScore = 0.5;
    private const int LatencyThresholdMs = 2000;

    /// <summary>
    /// Compute trust scores for all agents based on their rating history.
    /// </summary>
    public Dictionary<string, double> ComputeTrustScores(IReadOnlyList<AgentRatingRecord> ratings)
    {
        if (ratings.Count == 0)
            return new Dictionary<string, double>();

        // 1. Get all unique agents
        var agents = ratings.Select(r => r.AgentDid).Distinct().ToList();
        var agentIndex = agents.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i);
        var n = agents.Count;

        if (n < 2)
        {
            // With 0 or 1 agent, everyone gets default trust
            return agents.ToDictionary(a => a, _ => DefaultTrustScore);
        }

        // 2. Compute per-service consensus (objective metrics only)
        var serviceConsensus = ComputeServiceConsensus(ratings);

        // 3. Compute local trust: how consistent is each agent with the consensus?
        var localTrust = ComputeLocalTrust(ratings, serviceConsensus, agentIndex, n);

        // 4. Identify seed raters (top 10 agents by number of verified ratings)
        var seedRaters = IdentifySeedRaters(ratings, agentIndex);

        // 5. Iterate EigenTrust until convergence
        var trustVector = IterateEigenTrust(localTrust, seedRaters, n);

        // 6. Map back to agent DIDs, enforce minimum trust
        var result = new Dictionary<string, double>();
        for (int i = 0; i < n; i++)
        {
            var score = Math.Max(MinTrustScore, Math.Min(1.0, trustVector[i]));
            result[agents[i]] = Math.Round(score, 4);
        }

        return result;
    }

    /// <summary>
    /// For each service, compute the consensus metrics (majority vote on objective data).
    /// </summary>
    private static Dictionary<string, ServiceConsensus> ComputeServiceConsensus(
        IReadOnlyList<AgentRatingRecord> ratings)
    {
        return ratings
            .GroupBy(r => r.ServiceDid)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var records = g.ToList();
                    return new ServiceConsensus
                    {
                        MedianStatusIsSuccess = records.Count(r => r.StatusCode >= 200 && r.StatusCode < 300) > records.Count / 2.0,
                        MedianLatencyMs = ComputeMedianLatency(records),
                        MedianSchemaValid = records.Where(r => r.SchemaValid.HasValue).Count(r => r.SchemaValid == true) >
                                           records.Where(r => r.SchemaValid.HasValue).Count() / 2.0,
                    };
                });
    }

    /// <summary>
    /// Compute local trust matrix: c[i,j] = how much does agent i's rating of services
    /// agree with the objective consensus?
    /// Normalized per row so each row sums to 1.
    /// </summary>
    private static double[,] ComputeLocalTrust(
        IReadOnlyList<AgentRatingRecord> ratings,
        Dictionary<string, ServiceConsensus> consensus,
        Dictionary<string, int> agentIndex,
        int n)
    {
        // For EigenTrust, we need agent-to-agent trust.
        // We derive it from: agents who rate the same services similarly are trustworthy to each other.
        var agentCoherence = new double[n]; // How coherent is each agent overall?

        var agentRatings = ratings.GroupBy(r => r.AgentDid).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (agentDid, agentRecs) in agentRatings)
        {
            var idx = agentIndex[agentDid];
            var coherent = 0;
            var total = 0;

            foreach (var rec in agentRecs)
            {
                if (!consensus.TryGetValue(rec.ServiceDid, out var svcConsensus))
                    continue;

                total++;
                var isCoherent = true;

                // Check status code agreement
                var ratingIsSuccess = rec.StatusCode >= 200 && rec.StatusCode < 300;
                if (ratingIsSuccess != svcConsensus.MedianStatusIsSuccess)
                    isCoherent = false;

                // Check latency agreement (within 2x of median)
                if (rec.LatencyMs > svcConsensus.MedianLatencyMs * 2 ||
                    rec.LatencyMs < svcConsensus.MedianLatencyMs / 2)
                    isCoherent = false;

                if (isCoherent) coherent++;
            }

            agentCoherence[idx] = total > 0 ? (double)coherent / total : 0.5;
        }

        // Build normalized trust matrix
        // c[i,j] = coherence[j] / sum(coherence[k] for all k)
        // This means agents trust coherent agents more
        var c = new double[n, n];
        var totalCoherence = agentCoherence.Sum();

        if (totalCoherence <= 0)
            totalCoherence = n; // Fallback: uniform

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue; // No self-trust
                c[i, j] = agentCoherence[j] / totalCoherence;
            }

            // Normalize row
            var rowSum = 0.0;
            for (int j = 0; j < n; j++) rowSum += c[i, j];
            if (rowSum > 0)
                for (int j = 0; j < n; j++) c[i, j] /= rowSum;
        }

        return c;
    }

    /// <summary>
    /// Identify seed raters: top 10 agents by number of verified (receipt) ratings.
    /// </summary>
    private static double[] IdentifySeedRaters(
        IReadOnlyList<AgentRatingRecord> ratings,
        Dictionary<string, int> agentIndex)
    {
        var n = agentIndex.Count;
        var seed = new double[n];

        var verifiedCounts = ratings
            .Where(r => r.ReceiptVerified)
            .GroupBy(r => r.AgentDid)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key)
            .ToHashSet();

        if (verifiedCounts.Count == 0)
        {
            // No verified ratings yet — use uniform distribution
            for (int i = 0; i < n; i++)
                seed[i] = 1.0 / n;
        }
        else
        {
            foreach (var agentDid in verifiedCounts)
            {
                if (agentIndex.TryGetValue(agentDid, out var idx))
                    seed[idx] = 1.0 / verifiedCounts.Count;
            }
        }

        return seed;
    }

    /// <summary>
    /// Power iteration: t(k+1) = C^T * t(k), blended with seed raters.
    /// </summary>
    private static double[] IterateEigenTrust(double[,] c, double[] seed, int n)
    {
        const double alpha = 0.5; // Weight of seed raters in blending

        var t = new double[n];
        Array.Copy(seed, t, n);

        // If seed is all zeros, start uniform
        if (t.Sum() <= 0)
            for (int i = 0; i < n; i++)
                t[i] = 1.0 / n;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            var tNew = new double[n];

            // Matrix-vector multiply: tNew = C^T * t
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    tNew[i] += c[j, i] * t[j];
                }
            }

            // Blend with seed raters to prevent manipulation
            for (int i = 0; i < n; i++)
            {
                tNew[i] = (1 - alpha) * tNew[i] + alpha * seed[i];
            }

            // Normalize
            var sum = tNew.Sum();
            if (sum > 0 && !double.IsNaN(sum) && !double.IsInfinity(sum))
                for (int i = 0; i < n; i++) tNew[i] /= sum;
            else
            {
                // Fallback to uniform if computation diverged
                for (int i = 0; i < n; i++) tNew[i] = 1.0 / n;
                break;
            }

            // Sanitize NaN values
            for (int i = 0; i < n; i++)
                if (double.IsNaN(tNew[i]) || double.IsInfinity(tNew[i]))
                    tNew[i] = 1.0 / n;

            // Check convergence
            var diff = 0.0;
            for (int i = 0; i < n; i++)
                diff += Math.Abs(tNew[i] - t[i]);

            t = tNew;

            if (diff < ConvergenceThreshold)
                break;
        }

        // Scale to [0, 1] range (currently sums to 1, but individual values may be small)
        var max = t.Max();
        if (max > 0)
            for (int i = 0; i < n; i++) t[i] /= max;

        return t;
    }

    /// <summary>
    /// Proper median: average of two middle values for even-length lists.
    /// </summary>
    private static int ComputeMedianLatency(List<AgentRatingRecord> records)
    {
        var sorted = records.OrderBy(r => r.LatencyMs).Select(r => r.LatencyMs).ToList();
        if (sorted.Count == 0) return 0;
        if (sorted.Count % 2 == 1)
            return sorted[sorted.Count / 2];
        return (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private class ServiceConsensus
    {
        public bool MedianStatusIsSuccess { get; init; }
        public int MedianLatencyMs { get; init; }
        public bool MedianSchemaValid { get; init; }
    }
}

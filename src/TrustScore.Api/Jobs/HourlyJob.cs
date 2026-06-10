using Dapper;
using TrustScore.Api.Data;
using TrustScore.Api.Scoring;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Jobs;

/// <summary>
/// Hourly batch job that runs EigenTrust + Merkle tree anchoring.
/// Invoked via: dotnet TrustScore.Api.dll --job
/// Designed for Cloud Run Jobs triggered by Cloud Scheduler.
/// </summary>
public static class HourlyJob
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HourlyJob");

        try
        {
            logger.LogInformation("=== Hourly job started ===");

            // Probe first so the fresh measurements feed EigenTrust and get anchored this run.
            await RunSeedProbe(services, logger);
            await RunEigenTrust(services, logger);
            await RunMerkleAnchor(services, logger);

            logger.LogInformation("=== Hourly job completed successfully ===");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hourly job failed");
            return 1;
        }
    }

    private static async Task RunSeedProbe(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var prober = scope.ServiceProvider.GetRequiredService<SeedProber>();
        await prober.RunAsync();
    }

    private static async Task RunEigenTrust(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var ratingRepo = scope.ServiceProvider.GetRequiredService<IRatingRepository>();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();

        var engine = new EigenTrustEngine();
        var ratings = await ratingRepo.GetAllRatingsForTrustAsync();

        logger.LogInformation("EigenTrust: processing {Count} ratings", ratings.Count);

        if (ratings.Count < 2)
        {
            logger.LogInformation("EigenTrust: not enough ratings, skipping");
            return;
        }

        var scores = engine.ComputeTrustScores(ratings);
        await agentRepo.UpsertTrustScoresAsync(scores);

        logger.LogInformation("EigenTrust: updated trust scores for {Count} agents", scores.Count);
    }

    private static async Task RunMerkleAnchor(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var ratingRepo = scope.ServiceProvider.GetRequiredService<IRatingRepository>();
        var db = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        // Get all leaf hashes to build the tree
        var leaves = await ratingRepo.GetAllLeafHashesAsync();

        if (leaves.Count == 0)
        {
            logger.LogInformation("Merkle: no leaves to anchor");
            return;
        }

        var tree = new MerkleTree();
        foreach (var leaf in leaves)
        {
            var hash = MerkleTree.ComputeLeafHash(leaf.Id, leaf.ServiceDid, leaf.CreatedAt);
            tree.AddLeafHash(hash);
        }

        var rootHex = tree.RootHex!;
        logger.LogInformation("Merkle: computed root {Root} from {Count} leaves", rootHex, leaves.Count);

        // Store anchor in database
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO merkle_anchors (merkle_root, leaf_count, first_rating_id, last_rating_id, anchored_at)
            VALUES (@Root, @LeafCount, @FirstId, @LastId, NOW())
            """,
            new
            {
                Root = rootHex,
                LeafCount = leaves.Count,
                FirstId = leaves[0].Id,
                LastId = leaves[^1].Id,
            });

        logger.LogInformation("Merkle: anchor stored in database");

        // TODO Phase 2: Publish root to Base L2 blockchain
        // var txHash = await blockchainService.AnchorRootAsync(rootHex);
        // await conn.ExecuteAsync("UPDATE merkle_anchors SET blockchain='base', transaction_hash=@Tx WHERE merkle_root=@Root",
        //     new { Tx = txHash, Root = rootHex });
    }
}

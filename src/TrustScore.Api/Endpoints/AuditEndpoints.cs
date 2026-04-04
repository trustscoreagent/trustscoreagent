using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/audit/root", async (IAuditService auditService) =>
        {
            var anchor = await auditService.GetLatestAnchorAsync();

            if (anchor is null)
                return Results.Ok(new
                {
                    merkle_root = (string?)null,
                    leaf_count = 0,
                    anchored_at = (DateTimeOffset?)null,
                    blockchain = (string?)null,
                    message = "No anchors yet. The first anchor will be created within the next hour.",
                });

            return Results.Ok(new
            {
                merkle_root = anchor.MerkleRoot,
                leaf_count = anchor.LeafCount,
                anchored_at = anchor.AnchoredAt,
                blockchain = anchor.Blockchain,
                contract_address = anchor.ContractAddress,
                transaction_hash = anchor.TransactionHash,
                block_number = anchor.BlockNumber,
            });
        })
        .WithName("GetAuditRoot")
        .WithTags("Audit")
        .Produces(200)
        .WithOpenApi(op =>
        {
            op.Summary = "Get the latest Merkle tree root";
            op.Description = "Returns the latest anchored Merkle root hash, proving the integrity of all ratings. " +
                "When blockchain anchoring is active, includes the transaction hash and block number for on-chain verification.";
            return op;
        });
    }
}

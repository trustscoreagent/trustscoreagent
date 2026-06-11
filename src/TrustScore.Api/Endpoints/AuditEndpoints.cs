using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/audit/proof/{ratingId}", async (
            string ratingId,
            IAuditService auditService) =>
        {
            if (!Guid.TryParse(ratingId, out var id))
                return Results.BadRequest(new { error = "invalid_rating_id", message = "Rating ID must be a valid UUID" });

            var proof = await auditService.GetInclusionProofAsync(id);
            if (proof is null)
                return Results.NotFound(new { error = "not_found", message = "Rating not found or not yet included in Merkle tree" });

            return Results.Ok(new
            {
                rating_id = proof.RatingId,
                leaf_hash = proof.LeafHash,
                merkle_root = proof.MerkleRoot,
                proof = proof.Proof.Select(p => new { hash = p.Hash, is_right = p.IsRight }),
                leaf_index = proof.LeafIndex,
                total_leaves = proof.TotalLeaves,
                verification = "To verify: start with leaf_hash, for each proof node: if is_right, hash(current + node.hash), else hash(node.hash + current). Result should equal merkle_root.",
            });
        })
        .WithName("GetAuditProof")
        .WithTags("Audit")
        .Produces(200)
        .Produces(400)
        .Produces(404)
        .WithSummary("Get inclusion proof for a rating")
        .WithDescription("Returns a Merkle inclusion proof that cryptographically proves a specific rating exists in the audit log and has not been tampered with.");
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
        .WithSummary("Get the latest Merkle tree root")
        .WithDescription("Returns the latest anchored Merkle root hash, proving the integrity of all ratings. " +
            "When blockchain anchoring is active, includes the transaction hash and block number for on-chain verification.");
    }
}

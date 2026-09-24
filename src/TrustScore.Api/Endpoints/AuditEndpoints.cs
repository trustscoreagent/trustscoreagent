using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Endpoints;

public static class AuditEndpoints
{
    private const string SpecUrl =
        "https://github.com/trustscoreagent/trustscoreagent/blob/main/docs/MERKLE-SPEC.md";

    private static string VerificationSummary(MerkleTreeVersion version) => version == MerkleTreeVersion.V2
        ? "Rebuild the leaf from `committed` (see specification) and check it equals leaf_hash. Then, " +
          "starting from leaf_hash, for each proof node: if is_right, SHA256(0x01 || current || node.hash), " +
          "else SHA256(0x01 || node.hash || current). The result must equal merkle_root, which must equal " +
          "GET /v1/audit/root."
        : "Legacy v1 tree. Starting from leaf_hash, for each proof node: if is_right, " +
          "SHA256(current || node.hash), else SHA256(node.hash || current). The result must equal merkle_root.";

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
                leaf_version = proof.Leaf.LeafVersion,
                // What the leaf commits to, as stored today. Recompute the leaf from these rather than
                // trusting leaf_hash: if they do not hash to it, the rating was edited after it was
                // written.
                committed = proof.Leaf.Committed(),
                leaf_hash = proof.LeafHash,
                tree_version = (int)proof.TreeVersion,
                merkle_root = proof.MerkleRoot,
                proof = proof.Proof.Select(p => new { hash = p.Hash, is_right = p.IsRight }),
                leaf_index = proof.LeafIndex,
                total_leaves = proof.TotalLeaves,
                verification = VerificationSummary(proof.TreeVersion),
                specification = SpecUrl,
            });
        })
        .WithName("GetAuditProof")
        .WithTags("Audit")
        .Produces(200)
        .Produces(400)
        .Produces(404)
        .WithSummary("Get inclusion proof for a rating")
        .WithDescription("Returns a Merkle inclusion proof for a rating, with the fields its leaf commits to. " +
            "For ratings stored since Merkle v2 the leaf commits to what the rating reported (metrics, " +
            "verification flags, weight), so the proof shows it is in the anchored log unchanged.");
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

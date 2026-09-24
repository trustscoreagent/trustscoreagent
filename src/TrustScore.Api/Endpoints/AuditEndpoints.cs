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

        app.MapGet("/v1/audit/anchors", async (int? limit, int? before, IAuditService auditService) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var anchors = await auditService.GetAnchorsAsync(take, before);
            return Results.Ok(new
            {
                anchors = anchors.Select(AnchorJson),
                // Pass as ?before= for the next page.
                next_before = anchors.Count == take ? anchors[^1].Id : (int?)null,
            });
        })
        .WithName("ListAuditAnchors")
        .WithTags("Audit")
        .Produces(200)
        .WithSummary("History of anchored Merkle roots")
        .WithDescription("Every anchored root with its leaf count and tree version, newest first. " +
            "Record them over time: GET /v1/audit/consistency proves each later root extends an earlier one.");

        app.MapGet("/v1/audit/consistency", async (int? from, int? to, IAuditService auditService) =>
        {
            if (from is null || to is null)
                return Results.BadRequest(new
                {
                    error = "missing_anchor_ids",
                    message = "Pass the anchor ids to compare as ?from=<older id>&to=<newer id> (see GET /v1/audit/anchors).",
                });

            var result = await auditService.GetConsistencyProofAsync(from.Value, to.Value);
            return result.Status switch
            {
                ConsistencyProofStatus.Ok => Results.Ok(new
                {
                    from = AnchorJson(result.From!),
                    to = AnchorJson(result.To!),
                    tree_version = 2,
                    proof = result.Proof,
                    verification = "RFC 9162 section 2.1.4.2 with node = SHA256(0x01 || left || right), " +
                        "first = from.leaf_count, second = to.leaf_count. Take the roots and sizes from " +
                        "your own record of GET /v1/audit/anchors, not from this response.",
                    specification = SpecUrl,
                }),
                ConsistencyProofStatus.AnchorNotFound => Results.NotFound(new
                {
                    error = "anchor_not_found",
                    message = "No anchor with that id. List them with GET /v1/audit/anchors.",
                }),
                ConsistencyProofStatus.Unsupported => Results.UnprocessableEntity(new
                {
                    error = "unsupported",
                    message = "Consistency proofs exist only between tree_version 2 anchors, from the smaller " +
                        "(or equal) leaf_count to the larger. v1 anchors cannot be proven consistent with anything.",
                }),
                ConsistencyProofStatus.NotConsistent => Results.Conflict(new
                {
                    error = "not_consistent",
                    message = "The later anchor does not extend the earlier one: ratings covered by the earlier " +
                        "anchor were removed, reordered or altered.",
                    from = AnchorJson(result.From!),
                    to = AnchorJson(result.To!),
                }),
                _ => Results.Problem(
                    title: "snapshot_unavailable",
                    detail: "The later anchor's leaves cannot be reproduced right now, so no proof can be built.",
                    statusCode: 503),
            };
        })
        .WithName("GetAuditConsistency")
        .WithTags("Audit")
        .Produces(200)
        .Produces(400)
        .Produces(404)
        .Produces(409)
        .Produces(422)
        .WithSummary("Prove a later Merkle root extends an earlier one")
        .WithDescription("Returns an RFC 6962 consistency proof that anchor `from` is a prefix of anchor " +
            "`to`: the log only grew between them, and nothing it had committed to was removed or changed.");
    }

    private static object AnchorJson(MerkleAnchor a) => new
    {
        id = a.Id,
        merkle_root = a.MerkleRoot,
        leaf_count = a.LeafCount,
        tree_version = a.TreeVersion,
        cutoff_at = a.CutoffAt,
        anchored_at = a.AnchoredAt,
        blockchain = a.Blockchain,
        transaction_hash = a.TransactionHash,
        block_number = a.BlockNumber,
    };
}

using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        // Agent can check their own trust score
        app.MapGet("/v1/agent/trust", async (
            string? did,
            string? service,
            IAgentRepository agentRepo) =>
        {
            var raw = service ?? did;
            if (string.IsNullOrWhiteSpace(raw))
                return Results.BadRequest(new { error = "missing_agent", message = "Query parameter 'service' or 'did' with agent identifier is required" });

            // Agent DIDs are stored verbatim from the X-Agent-DID header (see RateEndpoints) and
            // upserted as-is by EigenTrust. They must NOT go through ServiceIdentifier.Normalize,
            // which is service-specific and mangles agent DIDs (e.g. "did:agent:xyz" → "did"),
            // making every lookup miss and always return the 0.5 default.
            var agentId = raw.Trim();
            if (agentId.Length > 500)
                return Results.BadRequest(new { error = "invalid_agent", message = "agent identifier too long (max 500 characters)" });

            var trustScore = await agentRepo.GetTrustScoreAsync(agentId);

            return Results.Ok(new
            {
                agent = agentId,
                trust_score = trustScore,
                interpretation = trustScore >= 0.8 ? "HIGH" :
                                trustScore >= 0.5 ? "MODERATE" :
                                trustScore >= 0.2 ? "LOW" : "VERY_LOW",
            });
        })
        .WithName("GetAgentTrust")
        .WithTags("Agent")
        .Produces(200)
        .WithOpenApi(op =>
        {
            op.Summary = "Get your agent's trust score";
            op.Description = "Returns the EigenTrust-computed trust score for an agent. Agents with high trust have more influence on service ratings.";
            return op;
        });

        // Trigger EigenTrust recalculation (will be replaced by Cloud Run Job in production)
        app.MapPost("/v1/admin/eigentrust", async (
            HttpContext httpContext,
            IConfiguration config,
            IRatingRepository ratingRepo,
            IAgentRepository agentRepo,
            ILogger<EigenTrustEngine> logger) =>
        {
            // API key authentication for admin endpoints
            var expectedKey = config["AdminApiKey"];
            var providedKey = httpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(expectedKey) || providedKey != expectedKey)
                return Results.Json(new { error = "unauthorized", message = "Valid X-Admin-Key header required" }, statusCode: 401);

            var engine = new EigenTrustEngine();

            var ratings = await ratingRepo.GetAllRatingsForTrustAsync();
            logger.LogInformation("Running EigenTrust on {Count} ratings", ratings.Count);

            var scores = engine.ComputeTrustScores(ratings);
            logger.LogInformation("Computed trust scores for {Count} agents", scores.Count);

            await agentRepo.UpsertTrustScoresAsync(scores);

            return Results.Ok(new
            {
                agents_updated = scores.Count,
                ratings_analyzed = ratings.Count,
                scores = scores.Select(s => new { agent = s.Key, trust_score = s.Value }),
            });
        })
        .WithName("RunEigenTrust")
        .WithTags("Admin")
        .Produces(200)
        .WithOpenApi(op =>
        {
            op.Summary = "Trigger EigenTrust recalculation (admin)";
            op.Description = "Recalculates trust scores for all agents based on rating consistency. In production, this runs automatically every hour via Cloud Scheduler.";
            return op;
        });
    }
}

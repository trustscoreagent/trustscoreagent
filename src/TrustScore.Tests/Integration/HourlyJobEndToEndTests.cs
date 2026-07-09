using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TrustScore.Api.Data;
using TrustScore.Api.Jobs;
using TrustScore.Api.Scoring;
using TrustScore.Core.Audit;
using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// The hourly job, end-to-end, against a real migrated PostgreSQL database: seed probe (stubbed
/// HTTP) → EigenTrust → Merkle anchoring, then the audit guarantee itself — every anchored
/// rating gets an inclusion proof that cryptographically verifies against the published root
/// (H2), and a post-cutoff rating does not. This orchestration had zero coverage before
/// (2026-07 audit, test debt).
/// </summary>
public class HourlyJobEndToEndTests : PostgresDatabaseTest
{
    private const string ProbeDid = "did:web:trustscoreagent.com:probe";
    private const int ProbeTimeoutSeconds = 3;

    private static Rating MakeRating(string serviceDid, string agentDid, DateTimeOffset createdAt) => new()
    {
        ServiceDid = serviceDid,
        AgentDid = agentDid,
        Metrics = new RatingMetrics { StatusCode = 200, LatencyMs = 120, SchemaValid = true },
        Weight = 1.0,
        CreatedAt = createdAt,
    };

    private ServiceProvider BuildJobServices(bool registerSeedProber)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Db);
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<IRatingRepository, RatingRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddSingleton<ICacheService, FakeCacheService>();
        services.AddSingleton<IScoringEngine>(new BetaReputationSystem());
        services.AddScoped<IRatingWriter, TransactionalRatingWriter>();

        if (registerSeedProber)
        {
            var handler = new StubHttpMessageHandler(request =>
                request.RequestUri!.Host == "probe-dead.test"
                    ? throw new HttpRequestException("unreachable")
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"ok"}""", Encoding.UTF8, "application/json"),
                    });
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
            services.AddSingleton(Options.Create(new SeedProbeOptions
            {
                Enabled = true,
                AgentDid = ProbeDid,
                TimeoutSeconds = ProbeTimeoutSeconds,
                Targets = new List<SeedProbeTarget>
                {
                    new() { Service = "probe-live.test", Url = "https://probe-live.test/v1", ExpectField = "status" },
                    new() { Service = "probe-dead.test", Url = "https://probe-dead.test/v1" },
                },
            }));
            services.AddScoped<SeedProber>();
        }

        return services.BuildServiceProvider();
    }

    private async Task InsertRatingsAsync(params Rating[] ratings)
    {
        var config = new ConfigurationBuilder().Build();
        var writer = new TransactionalRatingWriter(
            Db, new ServiceRepository(Db, config), new RatingRepository(Db));
        var engine = new BetaReputationSystem();
        foreach (var rating in ratings)
            await writer.SubmitAsync(rating.ServiceDid, engine.ComputeDelta(rating), rating);
    }

    [PostgresFact]
    public async Task FullPipeline_AnchorsPreCutoffRatings_AndEveryProofVerifiesAgainstThePublishedRoot()
    {
        var now = DateTimeOffset.UtcNow;
        var anchored = new[]
        {
            MakeRating("svc-one.test/api", "did:web:agent-a.test", now.AddMinutes(-12)),
            MakeRating("svc-one.test/api", "did:web:agent-b.test", now.AddMinutes(-11)),
            MakeRating("svc-two.test/api", "did:web:agent-a.test", now.AddMinutes(-9)),
            MakeRating("svc-two.test/api", "did:web:agent-b.test", now.AddMinutes(-7)),
        };
        var fresh = MakeRating("svc-one.test/api", "did:web:agent-a.test", now);
        await InsertRatingsAsync(anchored.Append(fresh).ToArray());

        using var provider = BuildJobServices(registerSeedProber: true);
        var exitCode = await HourlyJob.RunAsync(provider);
        exitCode.Should().Be(0);

        var ratingRepo = new RatingRepository(Db);
        var audit = new AuditService(Db, ratingRepo, new MemoryCache(new MemoryCacheOptions()));

        // The anchor covers exactly the pre-cutoff ratings (probe ratings are freshly created).
        var anchor = await audit.GetLatestAnchorAsync();
        anchor.Should().NotBeNull();
        anchor!.LeafCount.Should().Be(anchored.Length);
        anchor.CutoffAt.Should().NotBeNull();

        // The audit guarantee: every anchored rating has a proof that verifies against the root.
        foreach (var rating in anchored)
        {
            var proof = await audit.GetInclusionProofAsync(rating.Id);
            proof.Should().NotBeNull($"rating {rating.Id} is anchored");
            proof!.MerkleRoot.Should().Be(anchor.MerkleRoot);
            proof.TotalLeaves.Should().Be(anchored.Length);

            var verified = MerkleTree.VerifyProof(
                Convert.FromHexString(proof.LeafHash),
                proof.Proof.Select(p => new ProofNode(Convert.FromHexString(p.Hash), p.IsRight)).ToList(),
                Convert.FromHexString(anchor.MerkleRoot));
            verified.Should().BeTrue($"the inclusion proof of {rating.Id} must reconstruct the anchored root");
        }

        // A rating created after the cutoff is not yet provable — 404, never a wrong proof.
        (await audit.GetInclusionProofAsync(fresh.Id)).Should().BeNull();

        // The probe recorded real measurements: one healthy, one dead (M11 semantics in the DB).
        using var conn = Db.CreateConnection();
        var probeRows = (await conn.QueryAsync<(string ServiceDid, int StatusCode, int LatencyMs, bool? SchemaValid)>(
            "SELECT service_did, status_code, latency_ms, schema_valid FROM ratings WHERE agent_did = @Did",
            new { Did = ProbeDid })).ToList();
        probeRows.Should().HaveCount(2);
        var live = probeRows.Single(r => r.ServiceDid == "probe-live.test");
        live.StatusCode.Should().Be(200);
        live.SchemaValid.Should().BeTrue();
        var dead = probeRows.Single(r => r.ServiceDid == "probe-dead.test");
        dead.StatusCode.Should().Be(503);
        dead.LatencyMs.Should().Be(ProbeTimeoutSeconds * 1000);
        dead.SchemaValid.Should().BeNull();

        // EigenTrust persisted trust scores for the rating agents.
        var agentCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM agents WHERE did IN ('did:web:agent-a.test', 'did:web:agent-b.test')");
        agentCount.Should().Be(2);
    }
}

/// <summary>
/// A failing step must not take down the rest of the hour: with SeedProber unresolvable, the
/// job reports failure (exit 1) but EigenTrust and the Merkle anchor still run.
/// </summary>
public class HourlyJobStepIsolationTests : PostgresDatabaseTest
{
    [PostgresFact]
    public async Task SeedProbeFailure_DoesNotSkipEigenTrustNorTheMerkleAnchor()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new ConfigurationBuilder().Build();
        var writer = new TransactionalRatingWriter(
            Db, new ServiceRepository(Db, config), new RatingRepository(Db));
        var engine = new BetaReputationSystem();
        foreach (var agent in new[] { "did:web:agent-a.test", "did:web:agent-b.test" })
        {
            var rating = new Rating
            {
                ServiceDid = "svc-iso.test/api",
                AgentDid = agent,
                Metrics = new RatingMetrics { StatusCode = 200, LatencyMs = 100, SchemaValid = true },
                Weight = 1.0,
                CreatedAt = now.AddMinutes(-10),
            };
            await writer.SubmitAsync(rating.ServiceDid, engine.ComputeDelta(rating), rating);
        }

        // SeedProber (and its options) are deliberately NOT registered: the first step throws.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Db);
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<IRatingRepository, RatingRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddSingleton<ICacheService, FakeCacheService>();
        using var provider = services.BuildServiceProvider();

        var exitCode = await HourlyJob.RunAsync(provider);

        exitCode.Should().Be(1, "a failed step must surface in the job's exit code");

        using var conn = Db.CreateConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM merkle_anchors"))
            .Should().Be(1, "the Merkle anchor must run despite the SeedProbe failure");
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM agents"))
            .Should().BeGreaterThanOrEqualTo(2, "EigenTrust must run despite the SeedProbe failure");
    }
}

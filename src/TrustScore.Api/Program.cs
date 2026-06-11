using DbUp;
using StackExchange.Redis;
using TrustScore.Api.Data;
using TrustScore.Api.Endpoints;
using TrustScore.Api.Jobs;
using TrustScore.Api.Middleware;
using TrustScore.Api.Receipts;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Security: limit request body size
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // 1 MB max
});

// Configuration
var dbConnectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException("Connection string 'PostgreSQL' not configured");
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? "localhost:6379";

// Database
builder.Services.AddSingleton(new DbConnectionFactory(dbConnectionString));
builder.Services.AddScoped<IServiceRepository, ServiceRepository>();
builder.Services.AddScoped<IRatingRepository, RatingRepository>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();
builder.Services.AddScoped<IRatingWriter, TransactionalRatingWriter>();

// Redis — do not abort startup if Redis is unreachable; the app is designed to run in a
// degraded mode (PostgreSQL fallback) when Redis is down.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<ICacheService, RedisCacheService>();
builder.Services.AddSingleton<IRateLimiter, RedisRateLimiter>();

// Receipt verification. The did:web HTTP client routes through an SSRF-guarding connect
// callback that validates every resolved IP and refuses redirects.
builder.Services.AddHttpClient(DidWebResolver.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = SsrfGuard.ConnectAsync,
    });
builder.Services.AddSingleton<IDidResolver, DidWebResolver>();
builder.Services.AddSingleton<IReceiptVerifier, ReceiptVerifier>();

// Scoring
builder.Services.AddSingleton<IScoringEngine>(sp =>
    new BetaReputationSystem(sp.GetRequiredService<IConfiguration>()));

// Seed prober — establishes a real, auditable baseline by probing public APIs in the hourly job.
builder.Services.Configure<SeedProbeOptions>(builder.Configuration.GetSection(SeedProbeOptions.SectionName));
builder.Services.AddHttpClient(SeedProber.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TrustScoreAgent-Probe/0.1 (+https://trustscoreagent.com)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<SeedProber>();

// OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "TrustScoreAgent API",
        Version = "v1",
        Description = "Free, open reputation registry for AI microservices. Agents check trust scores before calling any service.",
        Contact = new() { Name = "TrustScoreAgent", Url = new Uri("https://trustscoreagent.com") },
        License = new() { Name = "Apache-2.0", Url = new Uri("https://www.apache.org/licenses/LICENSE-2.0") },
    });
});

// CORS — allow all origins (agents call from anywhere)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Job mode: run EigenTrust + Merkle anchoring then exit
if (args.Contains("--job"))
{
    var exitCode = await HourlyJob.RunAsync(app.Services);
    Environment.Exit(exitCode);
    return; // Unreachable but satisfies compiler
}

// Run database migrations (skip when running in test host)
var skipMigrations = builder.Configuration.GetValue<bool>("SkipMigrations");
if (!skipMigrations)
{
    // Walk up from the binary directory to find the migrations folder
    var migrationPath = FindMigrationsFolder();

    var upgrader = DeployChanges.To
        .PostgresqlDatabase(dbConnectionString)
        .WithScriptsFromFileSystem(migrationPath)
        .LogToConsole()
        .Build();

    var result = upgrader.PerformUpgrade();
    if (!result.Successful)
        throw new InvalidOperationException($"Database migration failed: {result.Error}");
}

// Middleware
app.UseCors();
app.UseMiddleware<GlobalRateLimitMiddleware>();

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map endpoints
app.MapHealthEndpoints();
app.MapScoreEndpoints();
app.MapRateEndpoints();
app.MapServicesEndpoints();
app.MapAuditEndpoints();
app.MapPremiumEndpoints();
app.MapAgentEndpoints();

// Static discovery files served from public/
app.MapGet("/llms.txt", () => Results.File("public/llms.txt", "text/plain"))
    .ExcludeFromDescription();
app.MapGet("/.well-known/agent.json", () => Results.File("public/.well-known/agent.json", "application/json"))
    .ExcludeFromDescription();

app.Run();

// Make Program accessible for integration tests
public partial class Program
{
    static string FindMigrationsFolder()
    {
        // Try from current working directory first (dotnet run scenario)
        var cwd = Directory.GetCurrentDirectory();
        var candidate = Path.Combine(cwd, "migrations");
        if (Directory.Exists(candidate)) return candidate;

        // Walk up from cwd to find repo root with migrations/
        var dir = new DirectoryInfo(cwd);
        while (dir != null)
        {
            candidate = Path.Combine(dir.FullName, "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        // Fallback: next to the binary (Docker scenario)
        candidate = Path.Combine(AppContext.BaseDirectory, "migrations");
        if (Directory.Exists(candidate)) return candidate;

        throw new DirectoryNotFoundException(
            $"Could not find 'migrations' folder. Searched from: {cwd}");
    }
}

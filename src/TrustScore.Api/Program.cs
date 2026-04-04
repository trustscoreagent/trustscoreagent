using DbUp;
using StackExchange.Redis;
using TrustScore.Api.Data;
using TrustScore.Api.Endpoints;
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

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(ConfigurationOptions.Parse(redisConnectionString)));
builder.Services.AddSingleton<ICacheService, RedisCacheService>();
builder.Services.AddSingleton<IRateLimiter, RedisRateLimiter>();

// Receipt verification
builder.Services.AddHttpClient<DidWebResolver>();
builder.Services.AddSingleton<IDidResolver, DidWebResolver>();
builder.Services.AddSingleton<IReceiptVerifier, ReceiptVerifier>();

// Scoring
builder.Services.AddSingleton<IScoringEngine, BetaReputationSystem>();

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

app.UseSwagger();
app.UseSwaggerUI();

// Map endpoints
app.MapHealthEndpoints();
app.MapScoreEndpoints();
app.MapRateEndpoints();
app.MapServicesEndpoints();
app.MapAuditEndpoints();
app.MapPremiumEndpoints();
app.MapAgentEndpoints();

// Cache headers for static files
app.MapGet("/llms.txt", () => Results.File("public/llms.txt", "text/plain"))
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

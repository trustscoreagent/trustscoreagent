using DbUp;
using StackExchange.Redis;
using TrustScore.Api.Data;
using TrustScore.Api.Endpoints;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var dbConnectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException("Connection string 'PostgreSQL' not configured");
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? "localhost:6379";

// Database
builder.Services.AddSingleton(new DbConnectionFactory(dbConnectionString));
builder.Services.AddScoped<IServiceRepository, ServiceRepository>();
builder.Services.AddScoped<IRatingRepository, RatingRepository>();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(ConfigurationOptions.Parse(redisConnectionString)));

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

// Run database migrations
if (args.Contains("--migrate") || app.Environment.IsDevelopment())
{
    var migrationPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "migrations");
    if (!Directory.Exists(migrationPath))
        migrationPath = Path.Combine(AppContext.BaseDirectory, "migrations");

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map endpoints
app.MapHealthEndpoints();
app.MapScoreEndpoints();
app.MapRateEndpoints();

// Cache headers for static files
app.MapGet("/llms.txt", () => Results.File("public/llms.txt", "text/plain"))
    .ExcludeFromDescription();

app.Run();

// Make Program accessible for integration tests
public partial class Program { }

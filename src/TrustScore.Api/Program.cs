using DbUp;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;
using TrustScore.Api.Data;
using TrustScore.Api.Endpoints;
using TrustScore.Api.Jobs;
using TrustScore.Api.Middleware;
using TrustScore.Api.Receipts;
using TrustScore.Api.Scoring;
using TrustScore.Core.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Outside Development, log as JSON. The default console formatter writes log values verbatim, so a
// newline in user-controlled input (X-Agent-DID, service, a receipt DID) could forge extra log
// lines; the JSON formatter escapes control characters, closing that log-injection vector, and is
// also what Cloud Logging parses into structured fields.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
}

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
builder.Services.AddMemoryCache();
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
    // A small headroom over the per-request CancellationTokenSource (SeedProbeOptions.TimeoutSeconds,
    // default 10s) so the explicit token is what actually bounds each probe.
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TrustScoreAgent-Probe/0.1 (+https://trustscoreagent.com)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
})
    // Probe targets are server-configured, but a compromised or DNS-hijacked target could 302 to a
    // link-local/metadata address; route through the same SSRF guard as did:web resolution and
    // refuse redirects so a probe can never reach an internal endpoint.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = SsrfGuard.ConnectAsync,
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

// Forwarded headers — behind Cloud Run (and optionally Cloudflare), the real client IP and
// scheme arrive in X-Forwarded-For / X-Forwarded-Proto. Without this, RemoteIpAddress is the
// front-end's IP and every client shares one rate-limit bucket. ForwardLimit = number of trusted
// proxies appending to the chain: 1 for Cloud Run direct; raise to 2 when the Cloudflare proxy is
// enabled (set ForwardedHeaders:ForwardLimit). KnownProxies/Networks are cleared because the
// front-end IPs are dynamic and internal.
var forwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = forwardLimit;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// CORS — allow all origins (agents call from anywhere)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Job mode: run the probe + EigenTrust + Merkle anchoring then exit. Returning the code (rather
// than Environment.Exit) lets the host dispose cleanly and flush logs before the process ends.
if (args.Contains("--job"))
{
    var exitCode = await HourlyJob.RunAsync(app.Services);
    return exitCode;
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

// Middleware. ForwardedHeaders must run first so downstream code sees the real client IP.
app.UseForwardedHeaders();

// Translate unhandled exceptions into a clean JSON error. ArgumentException (e.g. an
// over-long or empty service identifier from ServiceIdentifier.Normalize) maps to 400; anything
// else is a generic 500 with no internal detail leaked.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var isBadRequest = feature?.Error is ArgumentException;
    context.Response.StatusCode = isBadRequest ? 400 : 500;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(isBadRequest
        ? """{"error":"invalid_request","message":"The request could not be processed."}"""
        : """{"error":"internal_error","message":"An unexpected error occurred."}""");
}));

if (app.Environment.IsProduction())
    app.UseHsts();

// Minimal security headers for a JSON API.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

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

// Static discovery files served from public/. Resolved to an absolute path: Results.File
// requires a rooted path (a relative one throws → 500), and the folder sits at a different
// place when run locally vs. in the container.
var publicFolder = FindPublicFolder();
app.MapGet("/llms.txt", () => ServePublicFile(publicFolder, "llms.txt", "text/plain"))
    .ExcludeFromDescription();
app.MapGet("/.well-known/agent.json", () => ServePublicFile(publicFolder, Path.Combine(".well-known", "agent.json"), "application/json"))
    .ExcludeFromDescription();

app.Run();
return 0;

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

    // Locate the public/ folder (CWD for `dotnet run`, content root in the container). Returns
    // null if not found, in which case the discovery routes return 404 rather than crashing.
    static string? FindPublicFolder()
    {
        var cwd = Directory.GetCurrentDirectory();
        var candidate = Path.Combine(cwd, "public");
        if (Directory.Exists(candidate)) return candidate;

        var dir = new DirectoryInfo(cwd);
        while (dir != null)
        {
            candidate = Path.Combine(dir.FullName, "public");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        candidate = Path.Combine(AppContext.BaseDirectory, "public");
        return Directory.Exists(candidate) ? candidate : null;
    }

    static IResult ServePublicFile(string? publicFolder, string relativePath, string contentType)
    {
        if (publicFolder is null) return Results.NotFound();
        var full = Path.Combine(publicFolder, relativePath);
        return File.Exists(full) ? Results.File(full, contentType) : Results.NotFound();
    }
}

using DbUp;
using Npgsql;
using TrustScore.Api.Data;
using Xunit;

namespace TrustScore.Tests.Integration;

/// <summary>
/// Discovers a reachable PostgreSQL server for integration tests: the CI service container
/// (via the same ConnectionStrings__PostgreSQL variable the app uses), then the local
/// docker-compose instance. Tests marked [PostgresFact] are skipped — not failed — when no
/// server is reachable, so `dotnet test` stays green on machines without Docker.
/// </summary>
public static class PostgresTestServer
{
    public const string SkipReason =
        "No reachable PostgreSQL (set ConnectionStrings__PostgreSQL or run `docker compose up -d`)";

    public static readonly string? MaintenanceConnectionString = Discover();

    private static string? Discover()
    {
        var candidates = new List<string>();
        var fromEnv = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            candidates.Add(fromEnv);
        // Local docker-compose (host port 5433) and CI service container (5432).
        candidates.Add("Host=localhost;Port=5433;Database=trustscore;Username=trustscore;Password=trustscore");
        candidates.Add("Host=localhost;Port=5432;Database=trustscore_test;Username=trustscore;Password=trustscore");

        foreach (var candidate in candidates)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(candidate) { Timeout = 3 };
                using var conn = new NpgsqlConnection(builder.ConnectionString);
                conn.Open();
                return builder.ConnectionString;
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
    }
}

/// <summary>A [Fact] that only runs when a PostgreSQL test server is reachable.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTestServer.MaintenanceConnectionString is null)
            Skip = PostgresTestServer.SkipReason;
    }
}

/// <summary>
/// Base class giving each test class its OWN throwaway database, created from scratch and
/// migrated with the real DbUp pipeline (all migrations/ scripts) — so the tests exercise the
/// exact production schema, then dropped. Test classes are serialized through the "postgres"
/// collection because concurrent CREATE DATABASE from the same template can fail in PostgreSQL.
/// </summary>
[Collection("postgres")]
public abstract class PostgresDatabaseTest : IAsyncLifetime
{
    private string? _databaseName;

    protected string ConnectionString { get; private set; } = string.Empty;
    protected DbConnectionFactory Db { get; private set; } = null!;

    public Task InitializeAsync()
    {
        // All tests in the class are [PostgresFact]-skipped; nothing to set up.
        if (PostgresTestServer.MaintenanceConnectionString is not { } maintenance)
            return Task.CompletedTask;

        _databaseName = $"tsa_test_{Guid.NewGuid():N}";
        using (var conn = new NpgsqlConnection(maintenance))
        {
            conn.Open();
            using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", conn);
            create.ExecuteNonQuery();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(maintenance)
        {
            Database = _databaseName,
        }.ConnectionString;

        var upgrade = DeployChanges.To
            .PostgresqlDatabase(ConnectionString)
            .WithScriptsFromFileSystem(FindMigrationsFolder())
            .LogToTrace()
            .Build()
            .PerformUpgrade();
        if (!upgrade.Successful)
            throw new InvalidOperationException($"Test database migration failed: {upgrade.Error}");

        Db = new DbConnectionFactory(ConnectionString);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_databaseName is null || PostgresTestServer.MaintenanceConnectionString is not { } maintenance)
            return Task.CompletedTask;

        // Pooled connections would block DROP DATABASE.
        NpgsqlConnection.ClearAllPools();
        using var conn = new NpgsqlConnection(maintenance);
        conn.Open();
        using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
        drop.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static string FindMigrationsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find 'migrations' walking up from {AppContext.BaseDirectory}");
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection
{
}

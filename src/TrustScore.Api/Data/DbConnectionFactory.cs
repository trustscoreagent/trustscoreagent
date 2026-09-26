using Dapper;
using Npgsql;
using System.Data;

namespace TrustScore.Api.Data;

public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    // Npgsql reads TIMESTAMPTZ as a UTC DateTime. Property-mapped entities (ServiceEntity) convert
    // implicitly, but positional records (RatingLeafInfo, RatingSummary) are materialized through
    // their constructor, where Dapper requires the exact parameter type — without this handler
    // every such query throws at runtime ("A parameterless default constructor or one matching
    // signature … is required"), killing /v1/audit/proof and the Merkle anchoring job.
    static DbConnectionFactory()
    {
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    /// <summary>Pool size used when the connection string does not set one.</summary>
    public const int DefaultMaxPoolSize = 3;

    /// <param name="maxPoolSize">
    /// Npgsql defaults to 100 connections per process. Production, staging and both batch jobs share
    /// one db-f1-micro instance with 25 connections (22 usable), so a traffic spike across a few
    /// instances would exhaust it and take everything down together. The pool is capped instead;
    /// a request that finds it full waits for a connection rather than failing. An explicit pool
    /// size in the connection string wins.
    /// </param>
    public DbConnectionFactory(string connectionString, int maxPoolSize = DefaultMaxPoolSize)
    {
        _connectionString = WithPoolCap(connectionString, maxPoolSize);
    }

    internal static string WithPoolCap(string connectionString, int maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var explicitlySet = connectionString.Contains("pool size", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("maxpoolsize", StringComparison.OrdinalIgnoreCase);
        if (!explicitlySet)
            builder.MaxPoolSize = maxPoolSize;
        return builder.ConnectionString;
    }

    public IDbConnection CreateConnection()
        => new NpgsqlConnection(_connectionString);

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            // TIMESTAMPTZ comes back Kind=Utc; a bare TIMESTAMP comes back Unspecified and is
            // treated as UTC (the whole schema and app run in UTC).
            DateTime { Kind: DateTimeKind.Utc } dt => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTime { Kind: DateTimeKind.Unspecified } dt => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset"),
        };

        // Pass the DateTimeOffset through unchanged so Npgsql's native parameter handling (used
        // by every existing write path) keeps applying.
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
            => parameter.Value = value;
    }
}

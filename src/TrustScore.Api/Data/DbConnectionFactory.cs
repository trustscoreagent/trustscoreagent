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

    public DbConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
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

using Npgsql;
using System.Data;

namespace TrustScore.Api.Data;

public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IDbConnection CreateConnection()
        => new NpgsqlConnection(_connectionString);
}

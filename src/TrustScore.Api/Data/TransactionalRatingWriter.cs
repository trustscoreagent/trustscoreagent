using TrustScore.Core.Interfaces;
using TrustScore.Core.Models;

namespace TrustScore.Api.Data;

/// <summary>
/// Writes a rating submission inside a single database transaction so a partial failure can
/// never desync the aggregate score from the ratings table or drop a rating from the audit tree.
/// </summary>
public sealed class TransactionalRatingWriter : IRatingWriter
{
    private readonly DbConnectionFactory _db;
    private readonly IServiceRepository _services;
    private readonly IRatingRepository _ratings;

    public TransactionalRatingWriter(DbConnectionFactory db, IServiceRepository services, IRatingRepository ratings)
    {
        _db = db;
        _services = services;
        _ratings = ratings;
    }

    public async Task SubmitAsync(string serviceId, RatingDelta delta, Rating rating)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        await _services.ApplyRatingAtomicAsync(conn, tx, serviceId, delta);
        await _ratings.InsertAsync(conn, tx, rating);
        tx.Commit();
    }
}

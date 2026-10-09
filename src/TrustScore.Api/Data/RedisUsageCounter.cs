using System.Globalization;
using StackExchange.Redis;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Data;

/// <summary>
/// Usage counters in Redis: one hash per day, field "{endpoint}|{client}", kept 100 days. Counting
/// is best effort (a Redis outage loses counts, never requests).
/// </summary>
public sealed class RedisUsageCounter : IUsageCounter
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(100);

    private readonly RedisKeyspace _redis;
    private readonly ILogger<RedisUsageCounter> _logger;

    public RedisUsageCounter(RedisKeyspace redis, ILogger<RedisUsageCounter> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private static string DayKey(DateOnly day) => "usage:" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // Fire and forget: the request never waits on Redis for a counter, even when Redis is slow.
    public Task RecordAsync(DateOnly day, string endpoint, string client)
    {
        try
        {
            var db = _redis.Database();
            var key = DayKey(day);
            db.HashIncrement(key, $"{endpoint}|{client}", flags: CommandFlags.FireAndForget);
            db.KeyExpire(key, Retention, ExpireWhen.HasNoExpiry, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Usage count lost (Redis unavailable)");
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<UsageCount>> ReadAsync(DateOnly from, DateOnly to)
    {
        var totals = new Dictionary<(string, string), long>();
        var db = _redis.Database();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            foreach (var entry in await db.HashGetAllAsync(DayKey(day)))
            {
                var parts = entry.Name.ToString().Split('|', 2);
                if (parts.Length != 2) continue;
                var pair = (parts[0], parts[1]);
                totals[pair] = totals.GetValueOrDefault(pair) + (long)entry.Value;
            }
        }
        return totals.Select(t => new UsageCount(t.Key.Item1, t.Key.Item2, t.Value))
            .OrderByDescending(c => c.Calls).ToList();
    }
}

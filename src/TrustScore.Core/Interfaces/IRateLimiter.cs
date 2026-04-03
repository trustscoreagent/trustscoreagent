namespace TrustScore.Core.Interfaces;

public interface IRateLimiter
{
    /// <summary>
    /// Check if the action is allowed and increment the counter.
    /// Returns true if allowed, false if rate limit exceeded.
    /// </summary>
    Task<RateLimitResult> CheckAsync(string key, int maxRequests, TimeSpan window);
}

public sealed record RateLimitResult(bool Allowed, int CurrentCount, int MaxRequests)
{
    public int Remaining => Math.Max(0, MaxRequests - CurrentCount);
}

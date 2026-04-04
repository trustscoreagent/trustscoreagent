namespace TrustScore.Core.Interfaces;

public interface ICacheService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, TimeSpan expiry);
    /// <summary>
    /// Atomically set key only if it does not exist. Returns true if set, false if key already existed.
    /// Used for nonce anti-replay: only the first caller wins.
    /// </summary>
    Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan expiry);
    Task RemoveAsync(string key);
    Task<bool> IsAvailableAsync();
}

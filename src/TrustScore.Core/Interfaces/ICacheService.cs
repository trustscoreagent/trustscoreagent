namespace TrustScore.Core.Interfaces;

public interface ICacheService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, TimeSpan expiry);
    Task RemoveAsync(string key);
    Task<bool> IsAvailableAsync();
}

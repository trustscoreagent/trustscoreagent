using TrustScore.Core.Models;

namespace TrustScore.Core.Interfaces;

public interface IServiceRepository
{
    Task<ServiceEntity?> GetByDidAsync(string did);
    Task UpsertAsync(ServiceEntity service);
    Task<bool> ExistsAsync(string did);
}

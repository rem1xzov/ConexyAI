using ConexyAI.Entity;

namespace ConexyAI.Repository;

public interface IConexyRepository
{
    Task<ConexyEntity> CreateOrGetAsync(ConexyEntity entity, CancellationToken ct = default);
    Task<ConexyEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ConexyEntity?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<ConexyEntity>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<int> GetRequestCountInWindowAsync(Guid userId, DateTime since, CancellationToken ct = default);
    Task UpdateAsync(ConexyEntity entity, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
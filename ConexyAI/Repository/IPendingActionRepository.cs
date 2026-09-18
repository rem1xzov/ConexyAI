using ConexyAI.Entity;

namespace ConexyAI.Repository;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
public interface IPendingActionRepository
{
    Task<PendingActionEntity> CreateAsync(PendingActionEntity entity, CancellationToken ct = default);
    Task<PendingActionEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(PendingActionEntity entity, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

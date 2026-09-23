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

    // TASK_ORPHAN_RECOVERY: добавлено 2026-09-23
    /// <summary>
    /// Marks every task still <c>Pending</c>/<c>Running</c> as <c>Failed</c> with <paramref name="reason"/>
    /// and returns their ids. Only valid at process start, before the worker takes a job: the queue is
    /// in-memory, so at that moment no such task can have a live run behind it.
    /// </summary>
    Task<IReadOnlyList<Guid>> FailUnfinishedAsync(string reason, CancellationToken ct = default);
}
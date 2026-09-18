using ConexyAI.Entity;

namespace ConexyAI.Repository;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public interface IUsageRepository
{
    Task<UserUsageCounterEntity?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Inserts or updates the user's usage counter row.</summary>
    Task UpsertAsync(UserUsageCounterEntity entity, CancellationToken ct = default);
}

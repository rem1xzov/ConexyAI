using ConexyAI.Entity;

namespace ConexyAI.Repository;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public interface IUserMemoryRepository
{
    Task<IReadOnlyList<UserMemoryFactEntity>> GetFactsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Deletes all facts for the user and inserts the new list in one transaction.</summary>
    Task ReplaceFactsAsync(Guid userId, IReadOnlyList<string> facts, CancellationToken ct = default);
}

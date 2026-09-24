using ConexyAI.Entity;

namespace ConexyAI.Repository;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public interface IUserMemoryRepository
{
    /// <summary>The user's active facts (tombstones of deleted facts excluded), oldest first.</summary>
    Task<IReadOnlyList<UserMemoryFactEntity>> GetFactsAsync(Guid userId, CancellationToken ct = default);

    // MEMORY_CONTROL: добавлено 2026-09-24 — ревью H5.
    /// <summary>Texts of the facts the user deleted — the extractor must never bring them back.</summary>
    Task<IReadOnlyList<string>> GetSuppressedTextsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Makes the user's active facts equal to <paramref name="facts"/>: unchanged facts keep their id
    /// and source, gone facts are removed, new ones are added with <paramref name="sourceChatId"/>.
    /// A text matching a deleted (suppressed) fact is never re-added.
    /// </summary>
    Task ReplaceFactsAsync(Guid userId, IReadOnlyList<string> facts, Guid? sourceChatId = null, CancellationToken ct = default);

    /// <summary>Deletes one fact of the user (keeps a tombstone). False when it is not theirs.</summary>
    Task<bool> SuppressFactAsync(Guid userId, Guid factId, CancellationToken ct = default);

    /// <summary>Deletes every active fact of the user (keeps tombstones).</summary>
    Task<int> SuppressAllAsync(Guid userId, CancellationToken ct = default);
}

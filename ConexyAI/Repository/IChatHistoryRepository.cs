using ConexyAI.Entity;

namespace ConexyAI.Repository;

/// <summary>
/// Persistence for the flash/pro dialog history, keyed by the stable <c>chatId</c>.
/// The agent (conexy-coder) path does not use this repository.
/// </summary>
public interface IChatHistoryRepository
{
    // HISTORY_SCOPING: добавлено 2026-09-22 — чтение обязано фильтроваться по владельцу (это
    // требование было в докблоке сущности, но не выполнялось).
    /// <summary>Returns the chat's messages in chronological order, for its owner only.</summary>
    Task<IReadOnlyList<ConexyChatMessageEntity>> GetMessagesAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>Appends a single user/assistant message to the chat history.</summary>
    Task AppendAsync(Guid userId, Guid chatId, string role, string content, CancellationToken ct = default);

    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    /// <summary>Counts user messages in a chat (used for the memory extraction batching).</summary>
    Task<int> CountUserMessagesAsync(Guid userId, Guid chatId, CancellationToken ct = default);
}

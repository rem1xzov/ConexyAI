using ConexyAI.Entity;

namespace ConexyAI.Repository;

/// <summary>
/// Persistence for the dialog history, keyed by the stable <c>chatId</c>.
/// <para>
/// CONVERSATION_SERVICE: it is deliberately used by <see cref="Service.ConversationService"/> ONLY.
/// The chat/students paths, the conexy-coder path and the memory extractor all go through that
/// service, so no business logic has to remember to read or write history — which is exactly how
/// the context was lost twice before. Do not inject this repository into a runner or worker again.
/// </para>
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

    // CROSS_CHAT_CONTEXT: добавлено 2026-09-23
    /// <summary>
    /// The user's most recently active chats other than <paramref name="excludeChatId"/>, newest
    /// first: how each started (first user message) and where it ended (last assistant reply).
    /// </summary>
    Task<IReadOnlyList<RecentChatSummary>> GetRecentChatsAsync(
        Guid userId, Guid excludeChatId, int limit, CancellationToken ct = default);

    // CHAT_SYNC: добавлено 2026-09-23
    /// <summary>
    /// EVERY chat this user owns, newest activity first. This is what lets the sidebar be rebuilt on
    /// a second device: the client's chat list used to exist only in that browser's localStorage.
    /// </summary>
    Task<IReadOnlyList<ChatListSummary>> GetChatsAsync(Guid userId, int limit, CancellationToken ct = default);
}

// CROSS_CHAT_CONTEXT: добавлено 2026-09-23
public sealed record RecentChatSummary(
    Guid ChatId,
    DateTime LastActivityAt,
    string? FirstUserMessage,
    string? LastAssistantMessage);

// CHAT_SYNC: добавлено 2026-09-23
/// <summary>One chat as a chat list needs it (see <see cref="IChatHistoryRepository.GetChatsAsync"/>).</summary>
public sealed record ChatListSummary(
    Guid ChatId,
    DateTime LastActivityAt,
    int MessageCount,
    string? FirstUserMessage,
    string? LastAssistantMessage);

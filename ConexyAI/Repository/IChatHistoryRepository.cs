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
    /// <param name="kind">
    /// CHAT_KIND_SYNC: the chat's mode ("chat" | "projects" | "students"), so a chat synced to
    /// another device reopens in the tab it was created in. Null keeps the row mode-less.
    /// </param>
    Task AppendAsync(Guid userId, Guid chatId, string role, string content, string? kind = null, CancellationToken ct = default);

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

    // CHAT_DELETE: добавлено 2026-09-23
    /// <summary>
    /// Physically deletes every stored message of one chat, for its owner only, and returns how many
    /// rows were removed.
    /// <para>
    /// The return value is the ownership proof callers need: a chat id belonging to somebody else
    /// deletes zero rows, so a caller must not touch anything else (such as the on-disk workspace,
    /// which is keyed by chat id and is NOT user-scoped) unless this is greater than zero.
    /// </para>
    /// </summary>
    Task<int> DeleteChatAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    // CHAT_RENAME: добавлено 2026-09-23
    /// <summary>
    /// Stores a user-chosen name for one chat, for its owner only, and returns how many rows were
    /// updated. Zero means "no such chat for this user" — same ownership gate as the delete path.
    /// </summary>
    Task<int> RenameChatAsync(Guid userId, Guid chatId, string title, CancellationToken ct = default);

    // CHAT_PIN: добавлено 2026-09-23
    /// <summary>
    /// Pins or unpins one chat for its owner and returns how many rows were updated. Zero means "no
    /// such chat for this user" — the same ownership gate as the rename and delete paths.
    /// </summary>
    Task<int> SetPinnedAsync(Guid userId, Guid chatId, bool isPinned, CancellationToken ct = default);

    // CHAT_SYNC_COMPLETE: добавлено 2026-09-24 — ревью H8.
    /// <summary>Every chat id the user has stored history for (complete, unbounded — ids only).</summary>
    Task<IReadOnlyList<Guid>> GetChatIdsAsync(Guid userId, CancellationToken ct = default);

    // HISTORY_REPLAY: добавлено 2026-09-24 — ревью M5.
    /// <summary>
    /// Removes the chat's last turn (its last user message and every answer after it) and returns how
    /// many rows went away. Used when a turn replaces the previous one (regenerate / resend).
    /// </summary>
    Task<int> RemoveLastTurnAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the text of the chat's last assistant message; false when the chat has none. Used by
    /// Continue, whose partial answer is already stored and must grow instead of being duplicated.
    /// </summary>
    Task<bool> ReplaceLastAssistantAsync(Guid userId, Guid chatId, string content, CancellationToken ct = default);
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
    string? LastAssistantMessage,
    // CHAT_KIND_SYNC: режим чата, если он был записан (иначе null — вызывающий использует фолбэк).
    string? Kind,
    // CHAT_RENAME: пользовательское имя чата, если его задавали (иначе null — берётся из первого
    // сообщения пользователя).
    string? Title,
    // CHAT_PIN: закреплён ли чат наверху сайдбара; сюда же приезжает закрепление с другого устройства.
    bool IsPinned,
    // CHAT_OWNERSHIP: модель последнего хода (из таблицы chat), null для старых чатов.
    string? Model = null);

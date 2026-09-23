using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

public class ChatHistoryRepository : IChatHistoryRepository
{
    private readonly DbConexy _context;

    public ChatHistoryRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ConexyChatMessageEntity>> GetMessagesAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        return await _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task AppendAsync(Guid userId, Guid chatId, string role, string content, string? kind = null, CancellationToken ct = default)
    {
        _context.ChatMessages.Add(new ConexyChatMessageEntity
        {
            ChatId = chatId,
            UserId = userId,
            Role = role,
            Content = content,
            // CHAT_KIND_SYNC: режим пишется вместе с сообщением, чтобы список чатов мог вернуть его
            // без отдельной таблицы метаданных и без join’а.
            Kind = kind
        });
        await _context.SaveChangesAsync(ct);
    }

    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    public async Task<int> CountUserMessagesAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        return await _context.ChatMessages
            .CountAsync(m => m.ChatId == chatId && m.UserId == userId && m.Role == "user", ct);
    }

    // CROSS_CHAT_CONTEXT: добавлено 2026-09-23 — one round trip: the chats are ranked by their last
    // message, and only two rows per chat (first user message, last answer) are materialized.
    public async Task<IReadOnlyList<RecentChatSummary>> GetRecentChatsAsync(
        Guid userId, Guid excludeChatId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return Array.Empty<RecentChatSummary>();

        var rows = await QueryChatSummariesAsync(userId, excludeChatId, limit, ct);
        return rows
            .Select(r => new RecentChatSummary(r.ChatId, r.LastActivityAt, r.FirstUser, r.LastAssistant))
            .ToList();
    }

    // CHAT_SYNC: добавлено 2026-09-23
    public async Task<IReadOnlyList<ChatListSummary>> GetChatsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return Array.Empty<ChatListSummary>();

        var rows = await QueryChatSummariesAsync(userId, excludeChatId: null, limit, ct);
        return rows
            .Select(r => new ChatListSummary(
                r.ChatId, r.LastActivityAt, r.MessageCount, r.FirstUser, r.LastAssistant, r.Kind, r.Title, r.IsPinned))
            .ToList();
    }

    /// <summary>
    /// Shared shape of both chat-summary queries: one round trip, grouped per chat, ordered by the
    /// most recent message. <paramref name="excludeChatId"/> is only used by cross-chat context,
    /// which must not include the chat it is collecting context for.
    /// </summary>
    private async Task<List<ChatSummaryRow>> QueryChatSummariesAsync(
        Guid userId, Guid? excludeChatId, int limit, CancellationToken ct)
    {
        var query = _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId);

        if (excludeChatId is { } excluded)
        {
            query = query.Where(m => m.ChatId != excluded);
        }

        // The projection stays anonymous on purpose: EF translates that shape, but a constructor call
        // inside the grouped Select fails translation outright.
        var rows = await query
            .GroupBy(m => m.ChatId)
            .Select(g => new
            {
                ChatId = g.Key,
                LastActivityAt = g.Max(m => m.CreatedAt),
                MessageCount = g.Count(),
                FirstUser = g.Where(m => m.Role == "user")
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
                LastAssistant = g.Where(m => m.Role == "assistant")
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
                // CHAT_KIND_SYNC: строки до появления колонки имеют Kind = null; Max выдаёт
                // единственное непустое значение, а если их нет — null.
                Kind = g.Max(m => m.Kind),
                // CHAT_RENAME: то же для пользовательского имени: переименование обновляет все
                // строки чата, а новые пишутся с null и потому его не перебивают.
                Title = g.Max(m => m.Title),
                // CHAT_PIN: у PostgreSQL нет MAX(bool), поэтому флаг сначала сводится к 0/1 —
                // закрепление (true) перебивает значение по умолчанию (false).
                IsPinned = g.Max(m => m.IsPinned ? 1 : 0) == 1,
            })
            .OrderByDescending(c => c.LastActivityAt)
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(r => new ChatSummaryRow(
                r.ChatId, r.LastActivityAt, r.MessageCount, r.FirstUser, r.LastAssistant, r.Kind, r.Title, r.IsPinned))
            .ToList();
    }

    private sealed record ChatSummaryRow(
        Guid ChatId,
        DateTime LastActivityAt,
        int MessageCount,
        string? FirstUser,
        string? LastAssistant,
        string? Kind,
        string? Title,
        bool IsPinned);

    // CHAT_DELETE: добавлено 2026-09-23
    public async Task<int> DeleteChatAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        // Load-and-remove instead of ExecuteDeleteAsync: the in-memory provider used by the test
        // suite does not support ExecuteDelete, and one chat's rows are a bounded set anyway. The
        // WHERE clause is the security boundary — a foreign chat id matches nothing here.
        var rows = await _context.ChatMessages
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return 0;
        }

        _context.ChatMessages.RemoveRange(rows);
        await _context.SaveChangesAsync(ct);
        return rows.Count;
    }

    // CHAT_RENAME: добавлено 2026-09-23
    public async Task<int> RenameChatAsync(Guid userId, Guid chatId, string title, CancellationToken ct = default)
    {
        // Every row of the chat carries the name, so one UPDATE covers the whole conversation and the
        // ownership check is the same filtered read the rest of this repository uses.
        var rows = await _context.ChatMessages
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var row in rows)
        {
            row.Title = title;
        }

        await _context.SaveChangesAsync(ct);
        return rows.Count;
    }

    // CHAT_PIN: добавлено 2026-09-23
    public async Task<int> SetPinnedAsync(Guid userId, Guid chatId, bool isPinned, CancellationToken ct = default)
    {
        // Like a rename, the flag lives on every row of the chat: one pass covers the conversation and
        // the ownership check is the same filtered read used across this repository.
        var rows = await _context.ChatMessages
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var row in rows)
        {
            row.IsPinned = isPinned;
        }

        await _context.SaveChangesAsync(ct);
        return rows.Count;
    }
}

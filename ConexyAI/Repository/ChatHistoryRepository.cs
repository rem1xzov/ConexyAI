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
            // M6: Postgres rejects \0 inside text — one stray NUL used to lose the whole turn.
            Content = content.Contains('\0') ? content.Replace("\0", string.Empty) : content,
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
    // CHAT_SYNC_COMPLETE: изменено 2026-09-24 — ревью H8/M18: закреплённые чаты входят в ответ всегда,
    // даже если по активности они за пределами лимита, и каждый чат несёт модель последнего хода.
    public async Task<IReadOnlyList<ChatListSummary>> GetChatsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
            return Array.Empty<ChatListSummary>();

        var rows = await QueryChatSummariesAsync(userId, excludeChatId: null, limit, ct);

        var pinnedIds = await _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsPinned)
            .Select(m => m.ChatId)
            .Distinct()
            .ToListAsync(ct);
        var missingPinned = pinnedIds.Except(rows.Select(r => r.ChatId)).ToList();
        if (missingPinned.Count > 0)
        {
            rows.AddRange(await QueryChatSummariesAsync(userId, excludeChatId: null, missingPinned.Count, ct, missingPinned));
        }

        var ids = rows.Select(r => r.ChatId).ToList();
        var models = await _context.Chats
            .AsNoTracking()
            .Where(c => c.UserId == userId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Model })
            .ToDictionaryAsync(c => c.Id, c => c.Model, ct);

        return rows
            .OrderByDescending(r => r.LastActivityAt)
            .Select(r => new ChatListSummary(
                r.ChatId, r.LastActivityAt, r.MessageCount, r.FirstUser, r.LastAssistant, r.Kind, r.Title, r.IsPinned,
                models.GetValueOrDefault(r.ChatId)))
            .ToList();
    }

    // CHAT_SHARE_LINK: добавлено 2026-09-24
    public async Task<ChatListSummary?> GetChatAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var rows = await QueryChatSummariesAsync(userId, excludeChatId: null, 1, ct, new[] { chatId });
        if (rows.Count == 0)
            return null;

        var r = rows[0];
        var model = await _context.Chats
            .AsNoTracking()
            .Where(c => c.Id == chatId && c.UserId == userId)
            .Select(c => c.Model)
            .FirstOrDefaultAsync(ct);
        return new ChatListSummary(r.ChatId, r.LastActivityAt, r.MessageCount, r.FirstUser, r.LastAssistant, r.Kind, r.Title, r.IsPinned, model);
    }

    // CHAT_SYNC_COMPLETE: добавлено 2026-09-24
    public async Task<IReadOnlyList<Guid>> GetChatIdsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.ChatId)
            .Distinct()
            .ToListAsync(ct);
    }

    // HISTORY_REPLAY: добавлено 2026-09-24
    public async Task<int> RemoveLastTurnAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var rows = await _context.ChatMessages
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var lastUser = rows.FindLastIndex(m => m.Role == "user");
        if (lastUser < 0)
            return 0;

        var doomed = rows.Skip(lastUser).ToList();
        _context.ChatMessages.RemoveRange(doomed);
        await _context.SaveChangesAsync(ct);
        return doomed.Count;
    }

    // HISTORY_REPLAY: добавлено 2026-09-24
    public async Task<bool> ReplaceLastAssistantAsync(Guid userId, Guid chatId, string content, CancellationToken ct = default)
    {
        var last = await _context.ChatMessages
            .Where(m => m.ChatId == chatId && m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (last is null || last.Role != "assistant")
            return false;

        last.Content = content.Contains('\0') ? content.Replace("\0", string.Empty) : content;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Shared shape of both chat-summary queries: one round trip, grouped per chat, ordered by the
    /// most recent message. <paramref name="excludeChatId"/> is only used by cross-chat context,
    /// which must not include the chat it is collecting context for.
    /// </summary>
    private async Task<List<ChatSummaryRow>> QueryChatSummariesAsync(
        Guid userId, Guid? excludeChatId, int limit, CancellationToken ct, IReadOnlyCollection<Guid>? onlyChatIds = null)
    {
        var query = _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId);

        if (onlyChatIds is not null)
        {
            query = query.Where(m => onlyChatIds.Contains(m.ChatId));
        }

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

        // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью M9/H5: вместе с историей уходят строки ходов
        // (промпт и ответ, иначе они оставались доступны по GET /api/conexy/{taskId}), карточки
        // подтверждения команд и факты памяти, извлечённые из этого чата.
        var tasks = await _context.Conexy
            .Where(t => t.ChatId == chatId && t.UserId == userId)
            .ToListAsync(ct);
        var actions = await _context.PendingActions
            .Where(a => a.ChatId == chatId && a.UserId == userId)
            .ToListAsync(ct);
        var facts = await _context.UserMemoryFacts
            .Where(f => f.SourceChatId == chatId && f.UserId == userId)
            .ToListAsync(ct);

        if (rows.Count + tasks.Count + actions.Count + facts.Count == 0)
        {
            return 0;
        }

        _context.ChatMessages.RemoveRange(rows);
        _context.Conexy.RemoveRange(tasks);
        _context.PendingActions.RemoveRange(actions);
        _context.UserMemoryFacts.RemoveRange(facts);
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

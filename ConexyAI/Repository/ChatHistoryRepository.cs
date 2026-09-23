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

    public async Task AppendAsync(Guid userId, Guid chatId, string role, string content, CancellationToken ct = default)
    {
        _context.ChatMessages.Add(new ConexyChatMessageEntity
        {
            ChatId = chatId,
            UserId = userId,
            Role = role,
            Content = content
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

        var rows = await _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.ChatId != excludeChatId)
            .GroupBy(m => m.ChatId)
            .Select(g => new
            {
                ChatId = g.Key,
                LastActivityAt = g.Max(m => m.CreatedAt),
                FirstUser = g.Where(m => m.Role == "user")
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
                LastAssistant = g.Where(m => m.Role == "assistant")
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
            })
            .OrderByDescending(c => c.LastActivityAt)
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(r => new RecentChatSummary(r.ChatId, r.LastActivityAt, r.FirstUser, r.LastAssistant))
            .ToList();
    }
}

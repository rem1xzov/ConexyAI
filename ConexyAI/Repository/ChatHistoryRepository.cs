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

    public async Task<IReadOnlyList<ConexyChatMessageEntity>> GetMessagesAsync(Guid chatId, CancellationToken ct = default)
    {
        return await _context.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChatId == chatId)
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
    public async Task<int> CountUserMessagesAsync(Guid chatId, CancellationToken ct = default)
    {
        return await _context.ChatMessages
            .CountAsync(m => m.ChatId == chatId && m.Role == "user", ct);
    }
}

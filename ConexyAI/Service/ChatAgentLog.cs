using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service;

// SHARE_TO_ADMIN: добавлено 2026-11-19 — долговременный лог действий агента для разбора бага админом.
// Раньше инструменты и их результаты жили только в SignalR и localStorage браузера.
public interface IChatAgentLog
{
    /// <summary>
    /// Records one executed tool call of a chat. Failures are the caller's to contain: the log must
    /// never break an agent turn.
    /// </summary>
    Task RecordToolAsync(
        Guid chatId,
        Guid? taskId,
        string tool,
        string? arguments,
        string? output,
        bool isError,
        CancellationToken ct = default);
}

public sealed class ChatAgentLog : IChatAgentLog
{
    /// <summary>Newest rows kept per chat; older actions are pruned on insert.</summary>
    public const int MaxPerChat = 500;

    private const int MaxArgumentsChars = 2000;
    private const int MaxOutputChars = 8000;
    private const string ImagePrefix = "IMAGE_BASE64:";

    private readonly DbConexy _db;

    public ChatAgentLog(DbConexy db)
    {
        _db = db;
    }

    public async Task RecordToolAsync(
        Guid chatId,
        Guid? taskId,
        string tool,
        string? arguments,
        string? output,
        bool isError,
        CancellationToken ct = default)
    {
        var next = await _db.ChatAgentEvents
            .Where(e => e.ChatId == chatId)
            .Select(e => (int?)e.Seq)
            .MaxAsync(ct) ?? 0;

        _db.ChatAgentEvents.Add(new ChatAgentEventEntity
        {
            ChatId = chatId,
            TaskId = taskId,
            Seq = next + 1,
            Kind = "tool",
            Tool = Trim(tool, 80),
            Arguments = Trim(arguments, MaxArgumentsChars),
            Output = RenderOutput(output),
            IsError = isError,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        // BOUND: держим только свежие действия — лог не должен расти без предела.
        var count = await _db.ChatAgentEvents.CountAsync(e => e.ChatId == chatId, ct);
        var excess = count - MaxPerChat;
        if (excess <= 0)
            return;

        var stale = await _db.ChatAgentEvents
            .Where(e => e.ChatId == chatId)
            .OrderBy(e => e.Seq)
            .Take(excess)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            _db.ChatAgentEvents.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Base64 screenshots never go into the durable log — the admin sees a placeholder.</summary>
    private static string? RenderOutput(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        if (output.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            var size = Math.Max(0, output.Length - ImagePrefix.Length);
            return $"[изображение, ~{size / 1024} КБ]";
        }
        return Trim(output, MaxOutputChars);
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Length <= max ? value : value[..max] + "…";
    }
}

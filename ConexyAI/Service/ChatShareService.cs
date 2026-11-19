using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// SHARE_TO_ADMIN: добавлено 2026-11-19 — «поделиться чатом с админом» для разбора бага: админ видит
// диалог, действия агента (chat_agent_event) и скриншот визуальной проблемы.
public interface IChatShareService
{
    /// <summary>Creates or refreshes the share-to-admin report of one chat (owner only).</summary>
    Task<ShareToAdminResponse> ShareAsync(Guid userId, Guid chatId, ShareToAdminRequest request, CancellationToken ct = default);

    /// <summary>Revokes the owner's share-to-admin report, if any.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>Admin inbox of shared chats.</summary>
    Task<AdminSharedChatsResponse> ListAsync(int page, int pageSize, string? status, string? search, CancellationToken ct = default);

    /// <summary>Full admin view of one report (transcript + agent events + screenshot).</summary>
    Task<AdminSharedChatDetail?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Closes one report with an optional admin note.</summary>
    Task<bool> CloseAsync(Guid id, string? note, CancellationToken ct = default);
}

public sealed class ChatShareService : IChatShareService
{
    /// <summary>Longest accepted user comment.</summary>
    public const int MaxCommentChars = 2000;

    /// <summary>Largest accepted screenshot (decoded bytes).</summary>
    public const int MaxScreenshotBytes = 8 * 1024 * 1024;

    /// <summary>Screenshot types the admin viewer can render inline.</summary>
    public static readonly IReadOnlySet<string> AllowedScreenshotTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };

    private readonly DbConexy _db;
    private readonly IAdminNotifier? _notifier;
    private readonly ILogger<ChatShareService> _logger;

    public ChatShareService(DbConexy db, ILogger<ChatShareService> logger, IAdminNotifier? notifier = null)
    {
        _db = db;
        _logger = logger;
        _notifier = notifier;
    }

    public async Task<ShareToAdminResponse> ShareAsync(
        Guid userId, Guid chatId, ShareToAdminRequest request, CancellationToken ct = default)
    {
        var comment = NormalizeComment(request.Comment);
        var (screenshot, contentType, fileName) = DecodeScreenshot(request);

        var chat = await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
        // CHAT_OWNERSHIP: чужой чат не подтверждаем — 404, чтобы id не превращался в оракул.
        if (chat is null || chat.DeletedAt is not null)
            throw new AuthException("chat_not_ready", "Чат ещё не создан — отправьте сообщение.", 400);
        if (chat.UserId != userId)
            throw new AuthException("chat_not_found", "Чат не найден.", 404);

        var report = await _db.ChatShareReports.FirstOrDefaultAsync(r => r.ChatId == chatId, ct);
        var now = DateTime.UtcNow;
        if (report is null)
        {
            report = new ChatShareReportEntity
            {
                ChatId = chatId,
                UserId = userId,
                CreatedAt = now,
            };
            _db.ChatShareReports.Add(report);
        }

        report.ChatTitle = await ResolveTitleAsync(chatId, ct);
        if (comment is not null)
            report.Comment = comment;
        // VISUAL_BUG: новый скриншот заменяет прежний; без скриншота в запросе старый сохраняется.
        if (screenshot is not null)
        {
            report.Screenshot = screenshot;
            report.ScreenshotContentType = contentType;
            report.ScreenshotFileName = fileName;
        }

        // Повторная жалоба снова открывает отчёт: пользователь сообщает о проблеме заново.
        report.Status = ChatShareStatus.Open;
        report.ClosedAt = null;
        report.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Chat {ChatId} shared with admins by user {UserId} (report {ReportId}, screenshot {HasScreenshot}).",
            chatId, userId, report.Id, report.Screenshot is not null);

        if (_notifier is not null)
        {
            try
            {
                await _notifier.ChatSharedAsync(userId, chatId, report.ChatTitle, comment, ct);
            }
            catch (Exception ex)
            {
                // ADMIN_ALERTS: уведомление не должно ломать сам шаринг.
                _logger.LogWarning(ex, "Admin chat-share notification failed for report {ReportId}.", report.Id);
            }
        }

        return new ShareToAdminResponse(report.Id, report.CreatedAt, report.Status);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var report = await _db.ChatShareReports.FirstOrDefaultAsync(r => r.ChatId == chatId && r.UserId == userId, ct);
        if (report is null)
            return false;
        _db.ChatShareReports.Remove(report);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Chat {ChatId} share-to-admin revoked by user {UserId}.", chatId, userId);
        return true;
    }

    public async Task<AdminSharedChatsResponse> ListAsync(
        int page, int pageSize, string? status, string? search, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query =
            from report in _db.ChatShareReports.AsNoTracking()
            join user in _db.Users.AsNoTracking() on report.UserId equals user.Id into users
            from user in users.DefaultIfEmpty()
            select new { report, user };

        if (string.Equals(status, ChatShareStatus.Open, StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.report.Status == ChatShareStatus.Open);
        else if (string.Equals(status, ChatShareStatus.Closed, StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.report.Status == ChatShareStatus.Closed);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(x =>
                (x.user != null && x.user.Email != null && x.user.Email.Contains(needle))
                || (x.user != null && x.user.GitHubUsername != null && x.user.GitHubUsername.Contains(needle))
                || (x.report.ChatTitle != null && x.report.ChatTitle.Contains(needle)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.report.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var chatIds = rows.Select(x => x.report.ChatId).ToList();
        var counts = chatIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await _db.ChatAgentEvents.AsNoTracking()
                .Where(e => chatIds.Contains(e.ChatId))
                .GroupBy(e => e.ChatId)
                .Select(g => new { ChatId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ChatId, x => x.Count, ct);

        var items = rows.Select(x => new AdminSharedChatSummary(
            x.report.Id,
            x.report.ChatId,
            x.report.UserId,
            x.user?.Email,
            x.user?.GitHubUsername,
            x.report.ChatTitle,
            x.report.Comment,
            x.report.Screenshot != null && x.report.Screenshot.Length > 0,
            x.report.Status,
            x.report.CreatedAt,
            counts.GetValueOrDefault(x.report.ChatId))).ToList();

        return new AdminSharedChatsResponse(total, page, pageSize, items);
    }

    public async Task<AdminSharedChatDetail?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var report = await _db.ChatShareReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
            return null;

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == report.UserId, ct);

        var messages = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == report.ChatId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatTranscriptMessageDto(m.Role, m.Content, m.CreatedAt))
            .ToListAsync(ct);

        var events = await _db.ChatAgentEvents.AsNoTracking()
            .Where(e => e.ChatId == report.ChatId)
            .OrderBy(e => e.Seq)
            .Select(e => new AgentEventDto(e.Seq, e.Kind, e.Tool, e.Arguments, e.Output, e.IsError, e.CreatedAt))
            .ToListAsync(ct);

        return new AdminSharedChatDetail(
            report.Id,
            report.ChatId,
            report.UserId,
            user?.Email,
            user?.GitHubUsername,
            report.ChatTitle,
            report.Comment,
            report.Status,
            report.AdminNote,
            report.CreatedAt,
            messages,
            events,
            report.Screenshot is { Length: > 0 } ? Convert.ToBase64String(report.Screenshot) : null,
            report.ScreenshotContentType,
            report.ScreenshotFileName);
    }

    public async Task<bool> CloseAsync(Guid id, string? note, CancellationToken ct = default)
    {
        var report = await _db.ChatShareReports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
            return false;

        report.Status = ChatShareStatus.Closed;
        report.AdminNote = NormalizeComment(note);
        report.ClosedAt = DateTime.UtcNow;
        report.UpdatedAt = report.ClosedAt.Value;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // --- helpers -----------------------------------------------------------------------------

    private static string? NormalizeComment(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > MaxCommentChars)
            throw new AuthException("comment_too_long", $"Комментарий длиннее {MaxCommentChars} символов.", 400);
        return text;
    }

    private static (byte[]? Bytes, string? ContentType, string? FileName) DecodeScreenshot(ShareToAdminRequest request)
    {
        var raw = request.ScreenshotBase64?.Trim();
        if (string.IsNullOrEmpty(raw))
            return (null, null, null);

        var type = request.ScreenshotContentType?.Trim();
        if (string.IsNullOrEmpty(type) || !AllowedScreenshotTypes.Contains(type))
            throw new AuthException("unsupported_screenshot", "Скриншот должен быть PNG, JPEG или WEBP.", 400);

        // Клиент может прислать data-url; base64 — это хвост после запятой.
        var comma = raw.IndexOf(',');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            raw = raw[(comma + 1)..];

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            throw new AuthException("invalid_screenshot", "Скриншот не является корректным base64.", 400);
        }

        if (bytes.Length == 0)
            return (null, null, null);
        if (bytes.Length > MaxScreenshotBytes)
            throw new AuthException("screenshot_too_large", "Скриншот больше 8 МБ.", 400);

        var fileName = string.IsNullOrWhiteSpace(request.ScreenshotFileName)
            ? "screenshot" + type switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg",
            }
            : request.ScreenshotFileName.Trim();
        if (fileName.Length > 255)
            fileName = fileName[..255];

        return (bytes, type, fileName);
    }

    private async Task<string?> ResolveTitleAsync(Guid chatId, CancellationToken ct)
    {
        var first = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Role == "user")
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(first))
            return null;

        var line = first.ReplaceLineEndings(" ").Trim();
        return line.Length <= 120 ? line : line[..120];
    }
}

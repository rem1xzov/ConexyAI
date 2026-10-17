using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service.Prompts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// SUPPORT: добавлено 2026-09-19
// SUPPORT_BOT: расширено 2026-10-07 — ИИ-бот отвечает по умолчанию, эскалация на оператора,
// «вернуться к боту» и «отменить обращение».
public interface ISupportService
{
    Task<SupportTicketDto> GetOrCreateTicketAsync(Guid userId, CancellationToken ct = default);
    Task<SupportTicketDto?> GetMyTicketAsync(Guid userId, CancellationToken ct = default);
    Task<SupportMessageDto> AddMessageAsync(Guid ticketId, Guid senderId, string content, bool isFromAdmin, CancellationToken ct = default);
    // SUPPORT_ATTACHMENTS: сообщение с картинками/файлами.
    Task<SupportMessageDto> AddMessageAsync(Guid ticketId, Guid senderId, string content, bool isFromAdmin,
        IReadOnlyList<SupportUpload> files, CancellationToken ct = default);
    // SUPPORT_MULTI: много обращений у одного пользователя.
    Task<IReadOnlyList<SupportTicketSummaryDto>> ListMyTicketsAsync(Guid userId, CancellationToken ct = default);
    Task<SupportTicketDto> CreateTicketAsync(Guid userId, string? subject, CancellationToken ct = default);
    Task<SupportTicketDto> GetMyTicketByIdAsync(Guid ticketId, Guid userId, CancellationToken ct = default);
    Task<SupportAttachment?> GetAttachmentAsync(Guid attachmentId, Guid userId, bool isAdmin, CancellationToken ct = default);
    // SUPPORT_BOT: переходы состояния тикета (только владелец).
    Task<SupportTicketDto> EscalateAsync(Guid ticketId, Guid userId, CancellationToken ct = default);
    Task<SupportTicketDto> ReturnToBotAsync(Guid ticketId, Guid userId, CancellationToken ct = default);
    Task<SupportTicketDto> CancelAsync(Guid ticketId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<AdminSupportTicketDto>> GetAdminTicketsAsync(string? status, string? search, CancellationToken ct = default);
    Task<SupportTicketDto?> GetAdminTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default);
}

public class SupportService : ISupportService
{
    // SUPPORT_BOT: тексты системных плашек состояния.
    private const string EscalationMessage =
        "Оператор добавлен в чат. Он видит вашу переписку и ответит в течение суток.";
    private const string ReturnToBotMessage =
        "Вы вернулись к боту. Оператор по-прежнему видит переписку.";
    private const string CancelledMessage = "Обращение закрыто.";

    // SUPPORT_MULTI / SUPPORT_ATTACHMENTS: пределы, чтобы поддержку нельзя было использовать как файловое хранилище.
    public const int MaxOpenTickets = 20;
    public const int MaxFilesPerMessage = 5;
    public const long MaxFileBytes = 10L * 1024 * 1024;
    public const int MaxContentChars = 8_000;

    // Исполняемые файлы не принимаем: поддержка не должна раздавать оператору программы.
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".hta", ".lnk", ".jar", ".apk", ".dll",
    };

    // Картинки, которые безопасно показывать прямо в чате (SVG — нет: в нём может быть скрипт).
    public static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
    };

    private readonly ISupportRepository _repository;
    private readonly IHubContext<ConexyHub> _hubContext;
    // SUPPORT_BOT: бот ходит в модель без инструментов — только текст.
    private readonly IConexyLlmClient _llm;
    private readonly ILogger<SupportService> _logger;

    public SupportService(
        ISupportRepository repository,
        IHubContext<ConexyHub> hubContext,
        IConexyLlmClient llm,
        ILogger<SupportService> logger)
    {
        _repository = repository;
        _hubContext = hubContext;
        _llm = llm;
        _logger = logger;
    }

    public async Task<SupportTicketDto> GetOrCreateTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await _repository.GetOpenTicketByUserIdAsync(userId, ct);
        var ticketId = existing?.Id ?? (await _repository.AddTicketAsync(new SupportTicket { UserId = userId }, ct)).Id;

        var ticket = await _repository.GetTicketWithMessagesAsync(ticketId, ct);
        return ToTicketDto(ticket!);
    }

    public async Task<SupportTicketDto?> GetMyTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await _repository.GetOpenTicketByUserIdAsync(userId, ct);
        if (existing is null)
            return null;

        var ticket = await _repository.GetTicketWithMessagesAsync(existing.Id, ct);
        return ticket is null ? null : ToTicketDto(ticket);
    }

    // SUPPORT_MULTI: добавлено 2026-10-18
    public async Task<IReadOnlyList<SupportTicketSummaryDto>> ListMyTicketsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _repository.GetUserTicketsAsync(userId, ct);
        return rows.Select(r => new SupportTicketSummaryDto(
            r.Ticket.Id, r.Ticket.Subject, r.Ticket.Status.ToString(), r.Ticket.BotActive, r.Ticket.CreatedAt, r.Ticket.LastMessageAt,
            r.Last is null ? null : r.Last.Content.Length > 0 ? Preview(r.Last.Content) : "📎 вложение",
            r.Last?.AuthorType.ToString(), r.Unread)).ToList();
    }

    public async Task<SupportTicketDto> CreateTicketAsync(Guid userId, string? subject, CancellationToken ct = default)
    {
        if (await _repository.CountOpenTicketsAsync(userId, ct) >= MaxOpenTickets)
            throw new AuthException("too_many_tickets",
                $"Открыто уже {MaxOpenTickets} обращений. Закройте решённые, чтобы создать новое.", 409);
        var clean = string.IsNullOrWhiteSpace(subject) ? null : Preview(subject.Trim(), 120);
        var ticket = await _repository.AddTicketAsync(new SupportTicket { UserId = userId, Subject = clean, UserLastReadAt = DateTime.UtcNow }, ct);
        return await ReloadTicketDtoAsync(ticket.Id, ct);
    }

    public async Task<SupportTicketDto> GetMyTicketByIdAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await _repository.GetTicketWithMessagesAsync(ticketId, ct);
        if (ticket is null)
            throw new AuthException("ticket_not_found", "Обращение не найдено.", 404);
        if (ticket.UserId != userId)
            throw new AuthException("forbidden", "Это не ваше обращение.", 403);
        await _repository.MarkReadAsync(ticketId, DateTime.UtcNow, ct);
        return ToTicketDto(ticket);
    }

    public async Task<SupportAttachment?> GetAttachmentAsync(Guid attachmentId, Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        var found = await _repository.GetAttachmentAsync(attachmentId, ct);
        if (found is not { } row || (!isAdmin && row.OwnerId != userId))
            return null; // чужое вложение неотличимо от несуществующего
        return row.Attachment;
    }

    public Task<SupportMessageDto> AddMessageAsync(Guid ticketId, Guid senderId, string content, bool isFromAdmin, CancellationToken ct = default) =>
        AddMessageAsync(ticketId, senderId, content, isFromAdmin, [], ct);

    public async Task<SupportMessageDto> AddMessageAsync(
        Guid ticketId, Guid senderId, string content, bool isFromAdmin, IReadOnlyList<SupportUpload> files, CancellationToken ct = default)
    {
        var ticket = await _repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            throw new AuthException("ticket_not_found", "Тикет не найден.", 404);

        if (!isFromAdmin && ticket.UserId != senderId)
            throw new AuthException("forbidden", "Можно писать только в свой тикет.", 403);

        // SUPPORT_BOT: закрытое обращение только для чтения — пользователь создаёт новое.
        if (ticket.Status == SupportTicketStatus.Closed)
            throw new AuthException("ticket_closed", "Обращение закрыто. Создайте новое.", 409);

        content = (content ?? string.Empty).Trim();
        if (content.Length == 0 && files.Count == 0)
            throw new AuthException("empty_message", "Пустое сообщение.", 400);
        if (content.Length > MaxContentChars)
            throw new AuthException("message_too_long", $"Сообщение длиннее {MaxContentChars} символов.", 400);
        var attachments = ValidateFiles(files);

        var message = await _repository.AddMessageAsync(new SupportMessage
        {
            TicketId = ticketId,
            SenderId = senderId,
            Content = content,
            AuthorType = isFromAdmin ? SupportMessageAuthor.Admin : SupportMessageAuthor.User,
            CreatedAt = DateTime.UtcNow,
            Attachments = attachments,
        }, ct);

        // SUPPORT_MULTI: тема обращения — из первого сообщения пользователя; своё сообщение он уже прочитал.
        if (!isFromAdmin)
        {
            if (ticket.Subject is null)
            {
                ticket.Subject = content.Length > 0 ? Preview(content, 80) : attachments.FirstOrDefault()?.FileName;
                await _repository.UpdateTicketAsync(ticket, ct);
            }
            await _repository.MarkReadAsync(ticketId, message.CreatedAt, ct);
        }

        await EmitAsync(ticketId, message, ct);

        if (isFromAdmin)
        {
            // SUPPORT_BOT (правило 8): оператор написал — бот замолкает до «Вернуться к боту».
            ticket.AdminActive = true;
            ticket.BotActive = false;
            await _repository.UpdateTicketAsync(ticket, ct);
        }
        else if (ticket.BotActive)
        {
            await GenerateBotReplyAsync(ticket, ct);
        }

        return ToMessageDto(message);
    }

    // SUPPORT_BOT: добавлено 2026-10-07
    public async Task<SupportTicketDto> EscalateAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await RequireOwnOpenTicketAsync(ticketId, userId, ct);

        // Повторное «Не помогло» в уже эскалированном состоянии ничего не делает (правило 5).
        if (ticket.Status == SupportTicketStatus.Escalated && !ticket.BotActive)
            return await ReloadTicketDtoAsync(ticketId, ct);

        ticket.Status = SupportTicketStatus.Escalated;
        ticket.EscalatedAt = DateTime.UtcNow;
        ticket.BotActive = false;
        await _repository.UpdateTicketAsync(ticket, ct);
        await AddSystemMessageAsync(ticketId, EscalationMessage, ct);
        return await ReloadTicketDtoAsync(ticketId, ct);
    }

    // SUPPORT_BOT: добавлено 2026-10-07 — «Вернуться к боту»: бот снова отвечает, оператор остаётся.
    public async Task<SupportTicketDto> ReturnToBotAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await RequireOwnOpenTicketAsync(ticketId, userId, ct);

        ticket.BotActive = true;
        ticket.AdminActive = false;
        await _repository.UpdateTicketAsync(ticket, ct);
        await AddSystemMessageAsync(ticketId, ReturnToBotMessage, ct);
        return await ReloadTicketDtoAsync(ticketId, ct);
    }

    // SUPPORT_BOT: добавлено 2026-10-07 — «Отменить обращение»: закрываем тикет.
    public async Task<SupportTicketDto> CancelAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await RequireOwnOpenTicketAsync(ticketId, userId, ct);

        ticket.Status = SupportTicketStatus.Closed;
        ticket.ClosedAt = DateTime.UtcNow;
        ticket.BotActive = false;
        await _repository.UpdateTicketAsync(ticket, ct);
        await AddSystemMessageAsync(ticketId, CancelledMessage, ct);
        return await ReloadTicketDtoAsync(ticketId, ct);
    }

    public async Task<IReadOnlyList<AdminSupportTicketDto>> GetAdminTicketsAsync(
        string? status, string? search, CancellationToken ct = default)
    {
        SupportTicketStatus? filter = status switch
        {
            "BotHandling" => SupportTicketStatus.BotHandling,
            "Escalated" => SupportTicketStatus.Escalated,
            "Closed" => SupportTicketStatus.Closed,
            _ => null
        };

        var tickets = await _repository.GetTicketsAsync(filter, search, ct);
        return tickets.Select(ToAdminTicketDto).ToList();
    }

    public async Task<SupportTicketDto?> GetAdminTicketAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await _repository.GetTicketWithMessagesAsync(ticketId, ct);
        return ticket is null ? null : ToTicketDto(ticket);
    }

    public Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default)
    {
        // Админское закрытие совпадает с «Отменить обращение»: статус Closed + отметка времени.
        return _repository.CloseTicketAsync(ticketId, ct);
    }

    // ---------------------------------------------------------------- bot

    // SUPPORT_BOT: бот отвечает только текстом, без инструментов; контекст — вся переписка.
    private async Task GenerateBotReplyAsync(SupportTicket ticket, CancellationToken ct)
    {
        try
        {
            var withMessages = await _repository.GetTicketWithMessagesAsync(ticket.Id, ct);
            if (withMessages is null) return;
            // SUPPORT_MULTI: пользователь сидит в этом чате — ответ бота не считается непрочитанным.

            var messages = BuildBotMessages(withMessages);
            var result = await _llm.SendChatAsync(
                ConexyModelType.ConexyV1Flash, messages, new List<object>(), ct: ct);

            var text = result.Message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            var bot = await _repository.AddMessageAsync(new SupportMessage
            {
                TicketId = ticket.Id,
                SenderId = null,
                Content = text,
                AuthorType = SupportMessageAuthor.Bot,
                CreatedAt = DateTime.UtcNow
            }, ct);
            await EmitAsync(ticket.Id, bot, ct);
            await _repository.MarkReadAsync(ticket.Id, bot.CreatedAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Бот — не критичный путь: сообщение пользователя уже сохранено, эскалация доступна.
            _logger.LogWarning(ex, "Support bot reply failed for ticket {TicketId}", ticket.Id);
        }
    }

    private static List<ChatMessage> BuildBotMessages(SupportTicket ticket)
    {
        var messages = new List<ChatMessage> { new("system", PromptFragments.SupportBot) };
        foreach (var m in ticket.Messages.OrderBy(x => x.CreatedAt))
        {
            switch (m.AuthorType)
            {
                case SupportMessageAuthor.User:
                    // SUPPORT_ATTACHMENTS: файлы бот не открывает, но знает о них (может предложить оператора).
                    var files = m.Attachments.Count == 0
                        ? string.Empty
                        : "\n[Пользователь приложил: " + string.Join(", ", m.Attachments.Select(a => a.FileName))
                          + ". Ты не видишь содержимое — если оно важно, предложи нажать «Не помогло», чтобы подключить оператора.]";
                    messages.Add(new ChatMessage("user", (m.Content + files).Trim()));
                    break;
                case SupportMessageAuthor.Bot:
                    messages.Add(new ChatMessage("assistant", m.Content));
                    break;
                // Оператора и системные плашки отдаём модели как контекст, а не как её реплики.
                case SupportMessageAuthor.Admin:
                    messages.Add(new ChatMessage("system", "[Оператор] " + m.Content));
                    break;
                default:
                    messages.Add(new ChatMessage("system", m.Content));
                    break;
            }
        }
        return messages;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<SupportTicket> RequireOwnOpenTicketAsync(Guid ticketId, Guid userId, CancellationToken ct)
    {
        var ticket = await _repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            throw new AuthException("ticket_not_found", "Тикет не найден.", 404);
        if (ticket.UserId != userId)
            throw new AuthException("forbidden", "Это не ваш тикет.", 403);
        if (ticket.Status == SupportTicketStatus.Closed)
            throw new AuthException("ticket_closed", "Обращение уже закрыто.", 409);
        return ticket;
    }

    private async Task AddSystemMessageAsync(Guid ticketId, string content, CancellationToken ct)
    {
        var message = await _repository.AddMessageAsync(new SupportMessage
        {
            TicketId = ticketId,
            SenderId = null,
            Content = content,
            AuthorType = SupportMessageAuthor.System,
            CreatedAt = DateTime.UtcNow
        }, ct);
        await EmitAsync(ticketId, message, ct);
    }

    private async Task<SupportTicketDto> ReloadTicketDtoAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await _repository.GetTicketWithMessagesAsync(ticketId, ct);
        return ToTicketDto(ticket!);
    }

    private Task EmitAsync(Guid ticketId, SupportMessage message, CancellationToken ct) =>
        _hubContext.Clients.Group(Group(ticketId)).SendAsync("OnSupportMessageReceived", ToMessageDto(message), ct);

    private static string Group(Guid ticketId) => $"support_{ticketId}";

    // SUPPORT_ATTACHMENTS: проверка файлов до записи. Тип картинки берём по сигнатуре, а не со слов браузера:
    // иначе под видом image/png можно было бы залить HTML и открыть его у оператора.
    private static List<SupportAttachment> ValidateFiles(IReadOnlyList<SupportUpload> files)
    {
        if (files.Count > MaxFilesPerMessage)
            throw new AuthException("too_many_files", $"Не больше {MaxFilesPerMessage} файлов в одном сообщении.", 400);
        var result = new List<SupportAttachment>();
        foreach (var file in files)
        {
            var name = SafeName(file.FileName);
            if (file.Data.Length == 0)
                throw new AuthException("empty_file", $"Файл «{name}» пустой.", 400);
            if (file.Data.LongLength > MaxFileBytes)
                throw new AuthException("file_too_large", $"Файл «{name}» больше 10 МБ.", 400);
            if (BlockedExtensions.Contains(Path.GetExtension(name)))
                throw new AuthException("file_type_blocked", $"Файлы типа {Path.GetExtension(name)} отправлять нельзя. Упакуйте в .zip, если нужно.", 400);

            result.Add(new SupportAttachment
            {
                FileName = name,
                ContentType = DetectImageType(file.Data) ?? "application/octet-stream",
                Size = file.Data.LongLength,
                Data = file.Data,
                CreatedAt = DateTime.UtcNow,
            });
        }
        return result;
    }

    private static string? DetectImageType(byte[] b) => b switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x42, 0x4D, ..] => "image/bmp",
        _ => null,
    };

    private static string SafeName(string name)
    {
        var clean = new string(Path.GetFileName(name ?? string.Empty)
            .Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (clean.Length == 0)
            clean = "file";
        return clean.Length > 200 ? clean[..150] + Path.GetExtension(clean) : clean;
    }

    private static string Preview(string text, int max = 120)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > max ? line[..max].TrimEnd() + "…" : line;
    }

    private static SupportMessageDto ToMessageDto(SupportMessage m) =>
        new(m.Id, m.TicketId, m.SenderId, m.Content, m.CreatedAt,
            m.AuthorType.ToString(), m.AuthorType == SupportMessageAuthor.Admin,
            m.Attachments.Select(a => new SupportAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size, InlineImageTypes.Contains(a.ContentType))).ToList());

    private static SupportTicketDto ToTicketDto(SupportTicket t) =>
        new(t.Id, t.UserId, t.Status.ToString(), t.BotActive, t.EscalatedAt, t.ClosedAt,
            t.CreatedAt, t.LastMessageAt,
            t.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto).ToList(),
            t.Subject);

    private static AdminSupportTicketDto ToAdminTicketDto(SupportTicket t)
    {
        var last = t.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
        return new(t.Id, t.UserId, t.User.Email, t.User.GitHubUsername, t.Status.ToString(), t.BotActive,
            t.CreatedAt, t.LastMessageAt,
            last is null ? null : (last.Content.Length > 0 ? Preview(last.Content) : "📎 вложение"),
            t.Subject);
    }
}

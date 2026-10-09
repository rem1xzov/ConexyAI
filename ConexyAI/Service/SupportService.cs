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

    public async Task<SupportMessageDto> AddMessageAsync(
        Guid ticketId, Guid senderId, string content, bool isFromAdmin, CancellationToken ct = default)
    {
        var ticket = await _repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            throw new AuthException("ticket_not_found", "Тикет не найден.", 404);

        if (!isFromAdmin && ticket.UserId != senderId)
            throw new AuthException("forbidden", "Можно писать только в свой тикет.", 403);

        // SUPPORT_BOT: закрытое обращение только для чтения — пользователь создаёт новое.
        if (ticket.Status == SupportTicketStatus.Closed)
            throw new AuthException("ticket_closed", "Обращение закрыто. Откройте поддержку заново.", 409);

        var message = await _repository.AddMessageAsync(new SupportMessage
        {
            TicketId = ticketId,
            SenderId = senderId,
            Content = content,
            AuthorType = isFromAdmin ? SupportMessageAuthor.Admin : SupportMessageAuthor.User,
            CreatedAt = DateTime.UtcNow
        }, ct);

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
                    messages.Add(new ChatMessage("user", m.Content));
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

    private static SupportMessageDto ToMessageDto(SupportMessage m) =>
        new(m.Id, m.TicketId, m.SenderId, m.Content, m.CreatedAt,
            m.AuthorType.ToString(), m.AuthorType == SupportMessageAuthor.Admin);

    private static SupportTicketDto ToTicketDto(SupportTicket t) =>
        new(t.Id, t.UserId, t.Status.ToString(), t.BotActive, t.EscalatedAt, t.ClosedAt,
            t.CreatedAt, t.LastMessageAt,
            t.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto).ToList());

    private static AdminSupportTicketDto ToAdminTicketDto(SupportTicket t) =>
        new(t.Id, t.UserId, t.User.Email, t.User.GitHubUsername, t.Status.ToString(), t.BotActive,
            t.CreatedAt, t.LastMessageAt,
            t.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault()?.Content);
}

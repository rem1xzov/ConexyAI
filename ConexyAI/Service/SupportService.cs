using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Repository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// SUPPORT: добавлено 2026-09-19
public interface ISupportService
{
    Task<SupportTicketDto> GetOrCreateTicketAsync(Guid userId, CancellationToken ct = default);
    Task<SupportTicketDto?> GetMyTicketAsync(Guid userId, CancellationToken ct = default);
    Task<SupportMessageDto> AddMessageAsync(Guid ticketId, Guid senderId, string content, bool isFromAdmin, CancellationToken ct = default);
    Task<IReadOnlyList<AdminSupportTicketDto>> GetAdminTicketsAsync(string? status, string? search, CancellationToken ct = default);
    Task<SupportTicketDto?> GetAdminTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default);
}

public class SupportService : ISupportService
{
    private readonly ISupportRepository _repository;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<SupportService> _logger;

    public SupportService(
        ISupportRepository repository,
        IHubContext<ConexyHub> hubContext,
        ILogger<SupportService> logger)
    {
        _repository = repository;
        _hubContext = hubContext;
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

        var message = await _repository.AddMessageAsync(new SupportMessage
        {
            TicketId = ticketId,
            SenderId = senderId,
            Content = content,
            IsFromAdmin = isFromAdmin,
            CreatedAt = DateTime.UtcNow
        }, ct);

        var dto = ToMessageDto(message);
        await _hubContext.Clients.Group(Group(ticketId)).SendAsync("OnSupportMessageReceived", dto, ct);
        return dto;
    }

    public async Task<IReadOnlyList<AdminSupportTicketDto>> GetAdminTicketsAsync(
        string? status, string? search, CancellationToken ct = default)
    {
        SupportTicketStatus? filter = status switch
        {
            "Open" => SupportTicketStatus.Open,
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

    public Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default) =>
        _repository.CloseTicketAsync(ticketId, ct);

    private static string Group(Guid ticketId) => $"support_{ticketId}";

    private static SupportMessageDto ToMessageDto(SupportMessage m) =>
        new(m.Id, m.TicketId, m.SenderId, m.Content, m.CreatedAt, m.IsFromAdmin);

    private static SupportTicketDto ToTicketDto(SupportTicket t) =>
        new(t.Id, t.UserId, t.Status.ToString(), t.CreatedAt, t.LastMessageAt,
            t.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto).ToList());

    private static AdminSupportTicketDto ToAdminTicketDto(SupportTicket t) =>
        new(t.Id, t.UserId, t.User.Email, t.User.GitHubUsername, t.Status.ToString(),
            t.CreatedAt, t.LastMessageAt,
            t.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault()?.Content);
}

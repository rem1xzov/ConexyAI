using ConexyAI.Entity;

namespace ConexyAI.Repository;

// SUPPORT: добавлено 2026-09-19
public interface ISupportRepository
{
    Task<SupportTicket?> GetOpenTicketByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<SupportTicket> AddTicketAsync(SupportTicket ticket, CancellationToken ct = default);
    Task<SupportTicket?> GetTicketWithMessagesAsync(Guid ticketId, CancellationToken ct = default);
    Task<SupportTicket?> GetTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task<SupportMessage> AddMessageAsync(SupportMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(SupportTicketStatus? status, string? search, CancellationToken ct = default);
    Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default);
}

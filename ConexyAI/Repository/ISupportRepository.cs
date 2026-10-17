using ConexyAI.Entity;

namespace ConexyAI.Repository;

// SUPPORT: добавлено 2026-09-19
public interface ISupportRepository
{
    /// <summary>Открытый (не Closed) тикет пользователя, если он есть (самый свежий).</summary>
    Task<SupportTicket?> GetOpenTicketByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<SupportTicket> AddTicketAsync(SupportTicket ticket, CancellationToken ct = default);
    /// <summary>Тикет с сообщениями и метаданными вложений (без байтов).</summary>
    Task<SupportTicket?> GetTicketWithMessagesAsync(Guid ticketId, CancellationToken ct = default);
    Task<SupportTicket?> GetTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task<SupportMessage> AddMessageAsync(SupportMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(SupportTicketStatus? status, string? search, CancellationToken ct = default);
    Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default);
    // SUPPORT_BOT: сохранение изменений тикета (статус, флаги бота/оператора, отметки времени).
    Task UpdateTicketAsync(SupportTicket ticket, CancellationToken ct = default);

    // SUPPORT_MULTI: все обращения пользователя для списка (последнее сообщение и счётчик непрочитанных).
    Task<IReadOnlyList<(SupportTicket Ticket, SupportMessage? Last, int Unread)>> GetUserTicketsAsync(Guid userId, CancellationToken ct = default);
    Task<int> CountOpenTicketsAsync(Guid userId, CancellationToken ct = default);
    Task MarkReadAsync(Guid ticketId, DateTime at, CancellationToken ct = default);

    // SUPPORT_ATTACHMENTS: вложение вместе с данными и владельцем тикета (для проверки доступа).
    Task<(SupportAttachment Attachment, Guid OwnerId)?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default);
}

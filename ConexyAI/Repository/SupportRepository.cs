using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Repository;

// SUPPORT: добавлено 2026-09-19
public class SupportRepository : ISupportRepository
{
    private readonly DbConexy _context;

    public SupportRepository(DbConexy context)
    {
        _context = context;
    }

    public async Task<SupportTicket?> GetOpenTicketByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        // SUPPORT_BOT: «открытый» = любой, кроме Closed (бот или эскалация).
        return await _context.SupportTickets
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.Status != SupportTicketStatus.Closed)
            .OrderByDescending(t => t.LastMessageAt)
            .FirstOrDefaultAsync(ct);
    }

    // SUPPORT_MULTI: добавлено 2026-10-18
    public async Task<IReadOnlyList<(SupportTicket Ticket, SupportMessage? Last, int Unread)>> GetUserTicketsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var rows = await _context.SupportTickets
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.LastMessageAt)
            .Take(100)
            .Select(t => new
            {
                Ticket = t,
                Last = t.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault(),
                // Непрочитанные — ответы бота и оператора после последнего просмотра.
                Unread = t.Messages.Count(m =>
                    (m.AuthorType == SupportMessageAuthor.Bot || m.AuthorType == SupportMessageAuthor.Admin)
                    && (t.UserLastReadAt == null || m.CreatedAt > t.UserLastReadAt)),
            })
            .ToListAsync(ct);
        return rows.Select(r => (r.Ticket, (SupportMessage?)r.Last, r.Unread)).ToList();
    }

    public Task<int> CountOpenTicketsAsync(Guid userId, CancellationToken ct = default) =>
        _context.SupportTickets.CountAsync(t => t.UserId == userId && t.Status != SupportTicketStatus.Closed, ct);

    public async Task MarkReadAsync(Guid ticketId, DateTime at, CancellationToken ct = default)
    {
        var ticket = await _context.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.UserLastReadAt >= at)
            return;
        ticket.UserLastReadAt = at;
        await _context.SaveChangesAsync(ct);
    }

    // SUPPORT_ATTACHMENTS: добавлено 2026-10-18
    public async Task<(SupportAttachment Attachment, Guid OwnerId)?> GetAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var row = await _context.SupportAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Select(a => new { Attachment = a, OwnerId = a.Message.Ticket.UserId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Attachment, row.OwnerId);
    }

    public async Task<SupportTicket> AddTicketAsync(SupportTicket ticket, CancellationToken ct = default)
    {
        await _context.SupportTickets.AddAsync(ticket, ct);
        await _context.SaveChangesAsync(ct);
        return ticket;
    }

    public async Task<SupportTicket?> GetTicketWithMessagesAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await _context.SupportTickets
            .AsNoTracking()
            .Include(t => t.Messages)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return null;

        // SUPPORT_ATTACHMENTS: только метаданные — байты файлов при открытии чата не грузим.
        var messageIds = ticket.Messages.Select(m => m.Id).ToList();
        var attachments = await _context.SupportAttachments
            .AsNoTracking()
            .Where(a => messageIds.Contains(a.MessageId))
            .Select(a => new SupportAttachment
            {
                Id = a.Id, MessageId = a.MessageId, FileName = a.FileName, ContentType = a.ContentType,
                Size = a.Size, CreatedAt = a.CreatedAt, Data = Array.Empty<byte>(),
            })
            .ToListAsync(ct);
        foreach (var message in ticket.Messages)
            message.Attachments = attachments.Where(a => a.MessageId == message.Id).OrderBy(a => a.CreatedAt).ToList();
        return ticket;
    }

    public async Task<SupportTicket?> GetTicketAsync(Guid ticketId, CancellationToken ct = default)
    {
        return await _context.SupportTickets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    public async Task<SupportMessage> AddMessageAsync(SupportMessage message, CancellationToken ct = default)
    {
        await _context.SupportMessages.AddAsync(message, ct);

        // Bump the ticket's last-message timestamp so the admin list sorts correctly.
        var ticket = await _context.SupportTickets.FirstOrDefaultAsync(t => t.Id == message.TicketId, ct);
        if (ticket is not null)
        {
            ticket.LastMessageAt = message.CreatedAt;
        }

        await _context.SaveChangesAsync(ct);
        return message;
    }

    public async Task<IReadOnlyList<SupportTicket>> GetTicketsAsync(
        SupportTicketStatus? status, string? search, CancellationToken ct = default)
    {
        IQueryable<SupportTicket> query = _context.SupportTickets
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.Messages);

        if (status is not null)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(t =>
                (t.User.Email != null && t.User.Email.ToLower().Contains(term)) ||
                (t.User.GitHubUsername != null && t.User.GitHubUsername.ToLower().Contains(term)));
        }

        return await query
            .OrderByDescending(t => t.LastMessageAt)
            .ToListAsync(ct);
    }

    public async Task CloseTicketAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await _context.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
            return;

        ticket.Status = SupportTicketStatus.Closed;
        ticket.ClosedAt = DateTime.UtcNow;
        ticket.BotActive = false;
        await _context.SaveChangesAsync(ct);
    }

    // SUPPORT_BOT: добавлено 2026-10-07
    public async Task UpdateTicketAsync(SupportTicket ticket, CancellationToken ct = default)
    {
        var local = _context.SupportTickets.Local.FirstOrDefault(t => t.Id == ticket.Id);
        if (local is not null && !ReferenceEquals(local, ticket))
            _context.Entry(local).CurrentValues.SetValues(ticket);
        else if (local is null)
            _context.SupportTickets.Update(ticket);

        await _context.SaveChangesAsync(ct);
    }
}

namespace ConexyAI.Entity;

// SUPPORT: добавлено 2026-09-19
public enum SupportTicketStatus
{
    Open,
    Closed
}

// SUPPORT: добавлено 2026-09-19
/// <summary>
/// A support conversation between a user and an admin. One open ticket per user at a time;
/// the frontend can either create a new one or reuse the existing open ticket.
/// </summary>
public class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning user (FK to users).</summary>
    public Guid UserId { get; set; }

    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.Open;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

    /// <summary>Owning user navigation (for admin list display).</summary>
    public User User { get; set; } = null!;

    public ICollection<SupportMessage> Messages { get; set; } = new List<SupportMessage>();
}

// SUPPORT: добавлено 2026-09-19
/// <summary>A single message in a support ticket, sent by either the user or an admin.</summary>
public class SupportMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Parent ticket (FK to support_ticket).</summary>
    public Guid TicketId { get; set; }

    /// <summary>Sender (FK to users) — either the ticket owner or an admin.</summary>
    public Guid SenderId { get; set; }

    public string Content { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when the sender was an admin (distinguishes admin replies from user text).</summary>
    public bool IsFromAdmin { get; set; }

    public SupportTicket Ticket { get; set; } = null!;
}

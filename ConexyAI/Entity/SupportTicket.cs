namespace ConexyAI.Entity;

// SUPPORT_BOT: изменено 2026-10-07 — добавлены статусы бота и эскалации.
//   BotHandling — отвечает ИИ-бот (по умолчанию);
//   Escalated   — подключён оператор, бот молчит (или снова активен после «Вернуться к боту»);
//   Closed      — обращение закрыто.
public enum SupportTicketStatus
{
    BotHandling,
    Escalated,
    Closed
}

// SUPPORT_BOT: добавлено 2026-10-07 — кто автор сообщения. Заменяет прежний флаг IsFromAdmin:
// одного бита «админ/не админ» мало, теперь в переписке есть ещё бот и системные плашки.
public enum SupportMessageAuthor
{
    User,
    Bot,
    Admin,
    System
}

// SUPPORT: добавлено 2026-09-19
/// <summary>
/// A support conversation between a user, the AI bot and (after escalation) an admin. One open
/// (non-closed) ticket per user at a time; the frontend reuses it or creates a new one.
/// </summary>
public class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning user (FK to users).</summary>
    public Guid UserId { get; set; }

    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.BotHandling;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the user escalated to an operator (null until then).</summary>
    public DateTime? EscalatedAt { get; set; }

    /// <summary>When the user cancelled the request (null until closed).</summary>
    public DateTime? ClosedAt { get; set; }

    // SUPPORT_BOT: отвечает ли бот на сообщения пользователя. false — бот замолкает после эскалации
    // или после сообщения оператора и включается только по «Вернуться к боту».
    public bool BotActive { get; set; } = true;

    // SUPPORT_BOT: оператор писал в этом тикете. Сбрасывается при «Вернуться к боту» (правило 8).
    public bool AdminActive { get; set; }

    /// <summary>Owning user navigation (for admin list display).</summary>
    public User User { get; set; } = null!;

    public ICollection<SupportMessage> Messages { get; set; } = new List<SupportMessage>();
}

// SUPPORT: добавлено 2026-09-19
/// <summary>A single message in a support ticket, sent by the user, the AI bot, an admin or the system.</summary>
public class SupportMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Parent ticket (FK to support_ticket).</summary>
    public Guid TicketId { get; set; }

    /// <summary>
    /// Sender (FK to users). null for bot and system messages — there is no user behind them.
    /// </summary>
    public Guid? SenderId { get; set; }

    public string Content { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // SUPPORT_BOT: кто написал — пользователь, бот, оператор или система.
    public SupportMessageAuthor AuthorType { get; set; } = SupportMessageAuthor.User;

    public SupportTicket Ticket { get; set; } = null!;
}

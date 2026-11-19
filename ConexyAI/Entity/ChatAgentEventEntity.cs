namespace ConexyAI.Entity;

// SHARE_TO_ADMIN: добавлено 2026-11-19 — агентские действия (инструмент, аргументы, результат)
// раньше жили только в SignalR и localStorage. Здесь они сохраняются для разбора бага админом.
/// <summary>
/// One durable agent action of a chat: which tool ran, with what arguments, and what it returned.
/// Bounded per chat (the service keeps the newest <c>MaxPerChat</c> rows) so debugging data never
/// becomes an unbounded log.
/// </summary>
public class ChatAgentEventEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    /// <summary>Turn (task) the action belongs to; null for actions outside a turn.</summary>
    public Guid? TaskId { get; set; }

    /// <summary>Monotonic order inside the chat (best effort, assigned on insert).</summary>
    public int Seq { get; set; }

    /// <summary>Event kind; today only <c>tool</c>.</summary>
    public string Kind { get; set; } = "tool";

    public string? Tool { get; set; }

    /// <summary>Tool arguments as the model produced them, bounded.</summary>
    public string? Arguments { get; set; }

    /// <summary>Tool output, bounded; images are stored as a short placeholder, never as bytes.</summary>
    public string? Output { get; set; }

    public bool IsError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

namespace ConexyAI.Entity;

// CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1 (IDOR).
/// <summary>
/// One row per conversation: the single source of truth for who owns a chat id. Everything keyed by
/// the chat id alone — the workspace on disk, the SignalR workspace group, the IDE endpoints — checks
/// this row before doing anything, because the chat id itself is not a secret: "Share" hands it out.
/// <para>
/// The row is claimed by the first user who runs a turn (or writes a workspace file) in a fresh chat
/// and is never transferred. Deleting a chat keeps the row as a tombstone (<see cref="DeletedAt"/>) so
/// an in-flight turn cannot resurrect the chat and nobody can claim the id again.
/// </para>
/// </summary>
public class ChatEntity
{
    /// <summary>The chat id (frontend <c>ChatSession.id</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Owning user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Sidebar tab the chat lives in ("chat" | "projects" | "students").</summary>
    public string? Kind { get; set; }

    /// <summary>Public name of the model of the last turn (e.g. "conexy-cowork").</summary>
    public string? Model { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the owner deleted the chat; the content is gone, the tombstone stays.</summary>
    public DateTime? DeletedAt { get; set; }
}

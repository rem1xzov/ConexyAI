namespace ConexyAI.Entity;

/// <summary>
/// A single dialog message (user or assistant) for the flash/pro chat path.
/// Keyed by <see cref="ChatId"/> so the full conversation can be rebuilt across runs
/// in the same chat. The agent (conexy-coder) path does not use this table.
/// </summary>
public class ConexyChatMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable conversation id (frontend <c>ChatSession.id</c>).</summary>
    public Guid ChatId { get; set; }

    /// <summary><c>"user"</c> or <c>"assistant"</c>.</summary>
    public string Role { get; set; } = null!;

    public string Content { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

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

    // GITHUB_OAUTH: добавлено 2026-09-19 — owning user (FK to users).
    /// <summary>Owning user. Every read MUST be scoped to this id to prevent cross-user history access.</summary>
    public Guid UserId { get; set; }

    /// <summary><c>"user"</c> or <c>"assistant"</c>.</summary>
    public string Role { get; set; } = null!;

    public string Content { get; set; } = null!;

    // CHAT_KIND_SYNC: добавлено 2026-09-23
    /// <summary>
    /// Which mode this chat belongs to: <c>"chat"</c>, <c>"projects"</c> (the agent) or
    /// <c>"students"</c>. Stored on the history rows because that is the only table keyed by the
    /// chat id, and without it a chat synced to a second device cannot be placed in the right tab
    /// (the session kind used to live in the browser's localStorage only).
    /// <para>Null for rows written before this column existed — callers must fall back.</para>
    /// </summary>
    public string? Kind { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

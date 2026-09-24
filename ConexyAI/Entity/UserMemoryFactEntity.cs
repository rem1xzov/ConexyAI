namespace ConexyAI.Entity;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// A single durable user-memory fact extracted by the background LLM summarization.
/// Facts are stored as flat text (no embeddings / pgvector) and are replaced wholesale
/// on each extraction run.
/// </summary>
public class UserMemoryFactEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string FactText { get; set; } = null!;

    // MEMORY_CONTROL: добавлено 2026-09-24 — ревью H5.
    /// <summary>Chat the fact was first extracted from; the fact is deleted together with that chat.</summary>
    public Guid? SourceChatId { get; set; }

    /// <summary>
    /// The user deleted this fact. The row stays as a tombstone so the extractor never re-adds it from
    /// the same (still stored) dialog; it is never shown and never injected.
    /// </summary>
    public bool IsSuppressed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

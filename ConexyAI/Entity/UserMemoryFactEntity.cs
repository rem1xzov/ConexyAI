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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

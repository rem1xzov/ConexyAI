namespace ConexyAI.Entity;

public enum ConexyStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class ConexyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью M9: у строки хода есть чат, поэтому удаление
    // чата удаляет и его ходы (промпт и ответ), а не оставляет их доступными по GET /api/conexy/{id}.
    public Guid? ChatId { get; set; }

    public string Model { get; set; } = null!;
    public string Prompt { get; set; } = null!;
    public string? Result { get; set; }
    public ConexyStatus Status { get; set; } = ConexyStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
namespace ConexyAI.Entity;

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
public enum PendingActionStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Cancelled
}

/// <summary>
/// A dangerous <c>bash</c> command that has been paused awaiting user confirmation.
/// One row per agent command; <see cref="Status"/> advances from
/// <see cref="PendingActionStatus.Pending"/> once the user approves or rejects it.
/// </summary>
public class PendingActionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable conversation id (frontend <c>ChatSession.id</c>).</summary>
    public Guid ChatId { get; set; }

    // GITHUB_OAUTH: добавлено 2026-09-19 — owning user (FK to users).
    /// <summary>Owning user. Every read MUST be scoped to this id to prevent cross-user access.</summary>
    public Guid UserId { get; set; }

    /// <summary>Per-run task id (SignalR group).</summary>
    public Guid TaskId { get; set; }

    /// <summary>The raw shell command awaiting confirmation.</summary>
    public string Command { get; set; } = null!;

    /// <summary>Absolute working directory the command would run in.</summary>
    public string WorkingDirectory { get; set; } = null!;

    public PendingActionStatus Status { get; set; } = PendingActionStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAt { get; set; }
}

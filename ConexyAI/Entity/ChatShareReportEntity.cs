namespace ConexyAI.Entity;

// SHARE_TO_ADMIN: добавлено 2026-11-19 — пользователь делится чатом с админами, чтобы разработчик
// видел ход агента и мог починить баг. Одна активная запись на чат: повторный шаринг обновляет её.
public static class ChatShareStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>
/// One user request to let the admins inspect a chat. The row carries the user's bug comment and an
/// optional screenshot of the visual problem; the admin transcript and the agent's tool log are read
/// from the chat tables at view time.
/// </summary>
public class ChatShareReportEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Shared chat; one report per chat (unique index).</summary>
    public Guid ChatId { get; set; }

    /// <summary>Owner of the chat (the user who shared it).</summary>
    public Guid UserId { get; set; }

    /// <summary>Chat title snapshot for the admin list, so the list does not depend on chat history.</summary>
    public string? ChatTitle { get; set; }

    /// <summary>What the user says is wrong (free text, bounded in the service).</summary>
    public string? Comment { get; set; }

    /// <summary><see cref="ChatShareStatus"/> value.</summary>
    public string Status { get; set; } = ChatShareStatus.Open;

    /// <summary>Admin's note when the report is closed.</summary>
    public string? AdminNote { get; set; }

    // VISUAL_BUG: скриншот «как это выглядит» — хранится рядом с записью, как вложения поддержки.
    public byte[]? Screenshot { get; set; }
    public string? ScreenshotContentType { get; set; }
    public string? ScreenshotFileName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

namespace ConexyAI.Contract;

// SUPPORT: добавлено 2026-09-19
public record SupportMessageDto(
    Guid Id,
    Guid TicketId,
    Guid SenderId,
    string Content,
    DateTime CreatedAt,
    bool IsFromAdmin);

// SUPPORT: добавлено 2026-09-19
public record SupportTicketDto(
    Guid Id,
    Guid UserId,
    string Status,
    DateTime CreatedAt,
    DateTime LastMessageAt,
    IReadOnlyList<SupportMessageDto> Messages);

// SUPPORT: добавлено 2026-09-19 — one row in the admin support list.
public record AdminSupportTicketDto(
    Guid Id,
    Guid UserId,
    string? UserEmail,
    string? UserGitHubUsername,
    string Status,
    DateTime CreatedAt,
    DateTime LastMessageAt,
    string? LastMessagePreview);

// SUPPORT: добавлено 2026-09-19 — request body for posting a support message.
public record SupportMessageRequest(string Content);

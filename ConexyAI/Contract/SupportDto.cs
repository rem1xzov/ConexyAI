namespace ConexyAI.Contract;

// SUPPORT_BOT: изменено 2026-10-07 — добавлен AuthorType (User/Bot/Admin/System); SenderId теперь
// nullable (у бота и системных сообщений отправителя-пользователя нет). IsFromAdmin оставлен для
// админского UI, он выводится из AuthorType == Admin.
public record SupportMessageDto(
    Guid Id,
    Guid TicketId,
    Guid? SenderId,
    string Content,
    DateTime CreatedAt,
    string AuthorType,
    bool IsFromAdmin);

// SUPPORT_BOT: изменено 2026-10-07 — статус теперь BotHandling/Escalated/Closed, плюс флаг активности
// бота (по нему фронт показывает кнопки «Не помогло» / «Вернуться к боту»).
public record SupportTicketDto(
    Guid Id,
    Guid UserId,
    string Status,
    bool BotActive,
    DateTime? EscalatedAt,
    DateTime? ClosedAt,
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
    bool BotActive,
    DateTime CreatedAt,
    DateTime LastMessageAt,
    string? LastMessagePreview);

// SUPPORT: добавлено 2026-09-19 — request body for posting a support message.
public record SupportMessageRequest(string Content);

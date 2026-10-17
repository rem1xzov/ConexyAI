namespace ConexyAI.Contract;

// SUPPORT_ATTACHMENTS: добавлено 2026-10-18 — вложение без байтов; сами байты отдаёт отдельный эндпоинт.
public record SupportAttachmentDto(Guid Id, string FileName, string ContentType, long Size, bool IsImage);

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
    bool IsFromAdmin,
    IReadOnlyList<SupportAttachmentDto> Attachments);

// SUPPORT_BOT: изменено 2026-10-07 — статус теперь BotHandling/Escalated/Closed, плюс флаг активности
// бота (по нему фронт показывает кнопки «Не помогло» / «Вернуться к боту»).
// SUPPORT_MULTI: добавлена тема обращения.
public record SupportTicketDto(
    Guid Id,
    Guid UserId,
    string Status,
    bool BotActive,
    DateTime? EscalatedAt,
    DateTime? ClosedAt,
    DateTime CreatedAt,
    DateTime LastMessageAt,
    IReadOnlyList<SupportMessageDto> Messages,
    string? Subject);

// SUPPORT_MULTI: добавлено 2026-10-18 — строка в списке обращений пользователя (как чат в мессенджере).
public record SupportTicketSummaryDto(
    Guid Id,
    string? Subject,
    string Status,
    bool BotActive,
    DateTime CreatedAt,
    DateTime LastMessageAt,
    string? LastMessagePreview,
    string? LastMessageAuthor,
    int UnreadCount);

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
    string? LastMessagePreview,
    string? Subject);

// SUPPORT: добавлено 2026-09-19 — request body for posting a support message.
public record SupportMessageRequest(string Content);

// SUPPORT_MULTI: тело создания обращения (тема необязательна).
public record CreateSupportTicketRequest(string? Subject);

// SUPPORT_ATTACHMENTS: файл, пришедший в multipart-запросе.
public record SupportUpload(string FileName, string ContentType, byte[] Data);

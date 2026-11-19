namespace ConexyAI.Contract;

// SHARE_TO_ADMIN: добавлено 2026-11-19 — пользователь делится чатом с админами и при желании
// прикладывает скриншот визуального бага.
/// <summary>Body of <c>POST /api/conexy/chats/{chatId}/share-to-admin</c>.</summary>
/// <param name="Comment">What looks wrong, in the user's words. Null/empty is allowed.</param>
/// <param name="ScreenshotFileName">Original file name of the attached screenshot, if any.</param>
/// <param name="ScreenshotContentType">One of image/png, image/jpeg, image/webp.</param>
/// <param name="ScreenshotBase64">Raw base64 (no data-url prefix) of the screenshot bytes.</param>
public record ShareToAdminRequest(
    string? Comment,
    string? ScreenshotFileName,
    string? ScreenshotContentType,
    string? ScreenshotBase64);

/// <summary>Result of creating or refreshing a share-to-admin report.</summary>
public record ShareToAdminResponse(Guid Id, DateTime CreatedAt, string Status);

/// <summary>One durable agent action shown to the admin when debugging a shared chat.</summary>
public record AgentEventDto(
    int Seq,
    string Kind,
    string? Tool,
    string? Arguments,
    string? Output,
    bool IsError,
    DateTime CreatedAt);

/// <summary>One row of the admin "shared chats" list.</summary>
public record AdminSharedChatSummary(
    Guid Id,
    Guid ChatId,
    Guid UserId,
    string? UserEmail,
    string? UserGitHubUsername,
    string? ChatTitle,
    string? Comment,
    bool HasScreenshot,
    string Status,
    DateTime CreatedAt,
    int AgentEventCount);

/// <summary>Paginated admin "shared chats" list.</summary>
public record AdminSharedChatsResponse(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<AdminSharedChatSummary> Items);

/// <summary>Full admin view of one shared chat: transcript, agent actions and the screenshot.</summary>
public record AdminSharedChatDetail(
    Guid Id,
    Guid ChatId,
    Guid UserId,
    string? UserEmail,
    string? UserGitHubUsername,
    string? ChatTitle,
    string? Comment,
    string Status,
    string? AdminNote,
    DateTime CreatedAt,
    IReadOnlyList<ChatTranscriptMessageDto> Messages,
    IReadOnlyList<AgentEventDto> AgentEvents,
    string? ScreenshotBase64,
    string? ScreenshotContentType,
    string? ScreenshotFileName);

/// <summary>Body of <c>POST /api/admin/chats/shared/{id}/close</c>.</summary>
public record AdminCloseSharedChatRequest(string? Note);

namespace ConexyAI.Contract;

// CHAT_SYNC: добавлено 2026-09-23
//
// The chat list and the stored transcript, returned to the client so the sidebar can be rebuilt on
// a second device. Until now the chat list lived only in the browser's localStorage, which is why
// the desktop and the phone showed different conversations under the same account.

/// <summary>
/// One chat in the user's list. <see cref="Title"/> is derived from the first user message, so the
/// server does not need a separate chat-name table.
/// </summary>
/// <param name="Id">Stable chat id — the same value the client uses for the session and workspace.</param>
/// <param name="Kind">"chat" or "projects" (the agent mode), inferred from the chat's workspace.</param>
/// <param name="Title">Null when the chat has no user message yet; the client localizes the fallback.</param>
public record ChatSummaryDto(
    Guid Id,
    string? Title,
    string Kind,
    DateTime LastActivityAt,
    int MessageCount,
    string? LastMessage);

/// <summary>One stored turn. Only what the transcript needs: role, text and when it was stored.</summary>
public record ChatTranscriptMessageDto(string Role, string Content, DateTime CreatedAt);

/// <summary>The full stored transcript of one chat, oldest first.</summary>
public record ChatTranscriptDto(Guid Id, IReadOnlyList<ChatTranscriptMessageDto> Messages);

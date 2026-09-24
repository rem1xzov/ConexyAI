using ConexyAI.Model;

namespace ConexyAI.Contract;

public record ConexyJob(
    Guid TaskId,
    Guid ChatId,
    Guid UserId,
    ConexyModelType ModelType,
    string Prompt,
    string? GitHubToken = null,
    string? GitHubRepo = null,
    List<TaskAttachment>? Attachments = null,
    bool Thinking = false,
    string? ReasoningEffort = null,
    bool StudentsMode = false,
    bool SmartSearch = false,
    // INCOGNITO_CHAT: добавлено 2026-09-20
    // Ephemeral turn: keeps its context in memory only, never in the DB, and is excluded
    // from long-term memory both as a source and as a target.
    bool Incognito = false,
    // CONTINUE_GENERATION: добавлено 2026-09-21
    // Partial answer to resume from. The model receives it as its own truncated turn plus a
    // continue instruction, so it finishes the sentence instead of regenerating the reply.
    string? AssistantPrefix = null,
    // CHAT_KIND_SYNC: добавлено 2026-09-23 — режим чата ("chat" | "projects" | "students");
    // едет в ConversationContext и записывается вместе с ходом в историю.
    string? ChatKind = null,
    // HISTORY_REPLAY: добавлено 2026-09-24 — ход заменяет последний ход чата (ревью M5).
    bool Regenerate = false
);
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
    bool Incognito = false
);
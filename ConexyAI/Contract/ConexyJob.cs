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
    bool SmartSearch = false
);
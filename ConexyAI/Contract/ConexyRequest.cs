namespace ConexyAI.Contract;

public record ConexyRequest(
    string Model,
    string Prompt,
    string? GitHubToken = null,
    string? GitHubRepo = null,
    List<TaskAttachment>? Attachments = null,
    bool Thinking = false,
    string? ReasoningEffort = "high",
    bool StudentsMode = false,
    // Accepted for forward compatibility; the agent resolves these from tool calls today.
    string? WorkingDirectory = null,
    // Legacy task/session id (used for entity reuse/dedup). Kept for backward compatibility.
    string? SessionId = null,
    // Stable conversation id. This is the key for the on-disk workspace: one chat = one workspace.
    string? ChatId = null,
    // When true (flash/pro only), the web_search tool is passed to the model for this
    // message. conexy-coder always has web_search and ignores this flag.
    bool SmartSearch = false,
    // INCOGNITO_CHAT: добавлено 2026-09-20
    // When true the turn is never persisted: no ChatHistory/ConexyChatMessage row, no
    // long-term-memory read and no memory extraction. Subscription limits are still counted.
    bool Incognito = false,
    // CONTINUE_GENERATION: добавлено 2026-09-21
    // Already-generated answer text (the user pressed stop mid-reply). It is passed to the
    // model as its own assistant turn so it continues from there instead of starting over.
    string? AssistantPrefix = null,
    // CHAT_KIND_SYNC: добавлено 2026-09-23
    // Which tab the client created this chat in ("chat" | "projects" | "students"). Persisted with
    // the history so a chat synced to another device reopens in the same tab.
    string? ChatKind = null
);

/// <summary>An uploaded file/image attachment from the user.</summary>
public record TaskAttachment(
    string FileName,
    string ContentBase64,
    string ContentType // e.g. "image/png", "text/plain", "application/json"
);

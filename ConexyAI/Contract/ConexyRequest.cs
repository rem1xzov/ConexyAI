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
    string? ChatKind = null,
    // HISTORY_REPLAY: добавлено 2026-09-24 — ревью M5: «Сгенерировать заново»/«Отправить ещё раз»
    // заменяют последний ход чата, а не дописывают вторую копию сообщения пользователя.
    bool Regenerate = false,
    // ORCHESTRA: добавлено 2026-09-28 — пожелание клиента включить «Оркестр агентов». Решение
    // принимает сервер: оркестр доступен только ProMax и только режиму Coder (в остальных случаях
    // флаг просто снимается).
    bool Orchestra = false,
    // MCP: добавлено 2026-10-04 — личные MCP-серверы пользователя (URL + токен). Не хранятся на
    // сервере: едут с запросом, чтобы агент работал под аккаунтом пользователя, а не под общим.
    List<McpServerInput>? McpServers = null,
    // DESKTOP_AGENT: добавлено 2026-10-15 — выполнять инструменты на подключённом десктопе.
    bool UseDevice = false,
    // LOCAL_MCP: добавлено 2026-10-17 — инструменты, которые десктоп предлагает сам: локальные MCP-серверы
    // (stdio) и встроенные коннекторы (Google). Выполняются на ПК, сервер лишь показывает схемы модели.
    List<DeviceToolSpec>? DeviceTools = null,
    // WEB_DEVICE: добавлено 2026-10-18 — запуск из веба «на моём ПК»: проект десктопа, что делать,
    // если ПК офлайн ("fail" | "queue" | "sandbox"), и подтверждения/вопросы через веб.
    string? DeviceProjectId = null,
    string? DeviceOfflinePolicy = null,
    bool RemoteApprovals = false,
    // PHONE_AGENT: добавлено 2026-10-29 — выполнять инструменты phone_* на подключённом телефоне.
    bool UsePhone = false
);

// LOCAL_MCP: схема инструмента, которую прислал десктоп. Имя всегда local__<server>__<tool>.
public class DeviceToolSpec
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("inputSchema")]
    public System.Text.Json.JsonElement? InputSchema { get; set; }
}

// MCP: личный MCP-сервер пользователя. Токен живёт только в браузере и в памяти запроса.
public class McpServerInput
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Access token; sent as <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("token")]
    public string? Token { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>An uploaded file/image attachment from the user.</summary>
public record TaskAttachment(
    string FileName,
    string ContentBase64,
    string ContentType // e.g. "image/png", "text/plain", "application/json"
);

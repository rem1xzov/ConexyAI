using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

public class TodoItem
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; } // "pending" | "in_progress" | "completed" | "skipped"

    [JsonPropertyName("skip_reason")]
    public string? SkipReason { get; set; }
}

public class TodoWriteRequest
{
    [JsonPropertyName("todos")]
    public required List<TodoItem> Todos { get; set; }
}

public class TodoWriteResult
{
    public required bool Success { get; set; }
    public string? ErrorType { get; set; } // "invalid_status" | "missing_skip_reason" | "duplicate_id" | null
    public string? ErrorDetail { get; set; }
}

/// <summary>Full todo-list snapshot streamed to the UI after each successful todo_write.</summary>
public class TodoUpdateEvent
{
    public required List<TodoItem> Todos { get; set; }
}

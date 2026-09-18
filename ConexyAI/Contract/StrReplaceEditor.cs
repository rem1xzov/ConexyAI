using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

/// <summary>Arguments for the <c>str_replace_editor</c> tool (deserialized from the DeepSeek tool call).</summary>
public class StrReplaceEditorRequest
{
    [JsonPropertyName("command")]
    public required string Command { get; set; } // "view" | "create" | "str_replace" | "insert" | "undo"

    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("file_text")]
    public string? FileText { get; set; }

    [JsonPropertyName("old_str")]
    public string? OldStr { get; set; }

    [JsonPropertyName("new_str")]
    public string? NewStr { get; set; }

    [JsonPropertyName("insert_line")]
    public int? InsertLine { get; set; }

    [JsonPropertyName("view_range")]
    public int[]? ViewRange { get; set; }
}

/// <summary>Result returned as the tool result and streamed to the UI over SignalR.</summary>
public class StrReplaceEditorResult
{
    public required bool Success { get; set; }
    public string? Output { get; set; }
    public string? ErrorType { get; set; } // "file_not_found" | "ambiguous_match" | "no_match" | "invalid_range" | "io_error" | null
    public string? ErrorDetail { get; set; }
}

/// <summary>Live-action status event streamed to the UI before/after each editor command.</summary>
public class ToolActionEvent
{
    public required string ToolName { get; set; } // "str_replace_editor" | "bash" | "user"
    public required string Command { get; set; }
    public required string Path { get; set; }
    public required string Status { get; set; } // "started" | "completed" | "failed" | "pending_confirmation" | "rejected"
    public string? Summary { get; set; }

    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    /// <summary>Absolute working directory (populated for dangerous bash commands).</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Command output (populated on the completed event for dangerous commands).</summary>
    public string? Output { get; set; }

    /// <summary>Correlates the dangerous-command card with its pending confirmation.</summary>
    public Guid? PendingActionId { get; set; }
}

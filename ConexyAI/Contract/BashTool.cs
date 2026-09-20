using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

/// <summary>Arguments for the <c>bash</c> tool (deserialized from the DeepSeek tool call).</summary>
public class BashToolRequest
{
    [JsonPropertyName("command")]
    public required string Command { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; set; }

    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    /// <summary>Correlates the completion event with its pending confirmation card.</summary>
    [JsonPropertyName("pending_action_id")]
    public Guid? PendingActionId { get; set; }

    // COMMAND_CONFIRM: добавлено 2026-09-20
    /// <summary>
    /// Whether <see cref="IDangerousCommandClassifier"/> flagged this command. It no longer
    /// gates execution (every command is confirmed by the user) — it only drives the accent
    /// styling of the confirmation card and of the plain action rows.
    /// </summary>
    [JsonPropertyName("is_dangerous")]
    public bool IsDangerous { get; set; }
}

/// <summary>Result of a <c>bash</c> execution; <see cref="Output"/> is already truncated.</summary>
public class BashToolResult
{
    public required bool Success { get; set; }
    public required int ExitCode { get; set; }
    public required string Output { get; set; } // combined stdout+stderr, already truncated
    public bool WasTruncated { get; set; }
    public bool WasTimedOut { get; set; }
    public string? ErrorType { get; set; } // "timeout" | "workspace_not_found" | "spawn_failed" | null
}

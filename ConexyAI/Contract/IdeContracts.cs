using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

// ---------------------------------------------------------------------------
// File explorer REST contracts (request/response, not streamed over SignalR).
// ---------------------------------------------------------------------------

/// <summary>A single node in the file explorer tree.</summary>
public class FileNode
{
    public required string Name { get; set; }
    public required string Path { get; set; } // relative to the workspace root
    public required bool IsDirectory { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>Response payload for reading a file into the editor.</summary>
public class FileContentResponse
{
    public required string Path { get; set; }
    public required string Content { get; set; }
    public required bool IsBinary { get; set; } // when true, Content is empty and the UI renders a placeholder
}

/// <summary>Request payload for saving manual edits made by the user.</summary>
public class SaveFileRequest
{
    public required string Path { get; set; }
    public required string Content { get; set; }
}

/// <summary>Request payload for creating a file or directory.</summary>
public class CreateFileRequest
{
    public required string Path { get; set; }
    public required bool IsDirectory { get; set; }
}

/// <summary>Request payload for renaming/moving a file or directory.</summary>
public class RenameFileRequest
{
    public required string OldPath { get; set; }
    public required string NewPath { get; set; }
}

// ---------------------------------------------------------------------------
// Interactive terminal (SignalR) contracts.
// ---------------------------------------------------------------------------

/// <summary>Raw pty output forwarded from the server to the client. ANSI escape codes are preserved.</summary>
public class TerminalOutputEvent
{
    public required Guid SessionId { get; set; }
    public required string Data { get; set; }

    /// <summary>
    /// Origin of this chunk, so the UI can colour the source label differently:
    /// <c>"pty"</c> (interactive shell echo), <c>"agent"</c> (the agent's <c>bash</c> tool),
    /// <c>"user"</c> (the Run button / user-initiated command).
    /// </summary>
    public string Source { get; set; } = "pty";
}

/// <summary>Result of a Run-project request, returned to the client over SignalR.</summary>
public class RunProjectResult
{
    /// <summary>True when no run command could be auto-detected and the user must provide one.</summary>
    public required bool NeedsManualConfig { get; set; }

    /// <summary>The command that was (or will be) executed, when known.</summary>
    public string? Command { get; set; }

    /// <summary>Optional error detail for the UI.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// True when the run was launched in a separate visible console window (Windows dev
    /// workaround) instead of the interactive pty. The UI uses this to show a toast rather
    /// than switching to the terminal panel.
    /// </summary>
    public bool LaunchedInSeparateWindow { get; set; }
}

/// <summary>
/// Live progress signal for the chat <c>web_search</c> tool. The UI shows a transient
/// "searching…" indicator while <see cref="Status"/> is <c>"searching"</c>.
/// </summary>
public class SearchStatusEvent
{
    /// <summary><c>"searching"</c> or <c>"completed"</c>.</summary>
    public required string Status { get; set; }

    /// <summary>The search query being executed, when known.</summary>
    public string? Query { get; set; }
}

/// <summary>A single compiler/build diagnostic parsed from command output (MSBuild/csc format).</summary>
public class BuildProblem
{
    public required string File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public required string Severity { get; set; } // "error" | "warning"
    public required string Code { get; set; }
    public required string Message { get; set; }
}

/// <summary>Streamed to the client after a build-style bash command to populate the Problems panel.</summary>
public class BuildProblemsEvent
{
    public required List<BuildProblem> Problems { get; set; }
}

// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
/// <summary>
/// Streamed to the client when the agent attempts a dangerous <c>bash</c> command and is
/// paused awaiting user confirmation. The client shows a blocking modal and later calls
/// <c>ConfirmAction</c> on the hub with the same <see cref="ActionId"/>.
/// </summary>
public class PendingActionEvent
{
    public required Guid ActionId { get; set; }
    public required Guid ChatId { get; set; }
    public required Guid TaskId { get; set; }
    public required string Command { get; set; }
    public required string WorkingDirectory { get; set; }
    public required string Status { get; set; } // "Pending"
    public DateTime CreatedAt { get; set; }
}

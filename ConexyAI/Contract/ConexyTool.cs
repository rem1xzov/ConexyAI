namespace ConexyAI.Contract;

public record ConexyToolCall(
    string Id,
    string FunctionName,
    string ArgumentsJson
);

public record ConexyToolResult(
    string ToolCallId,
    string Output,
    bool IsError
)
{
    // SELF_CORRECTION: добавлено 2026-09-24
    /// <summary>
    /// True when a shell command actually ran and <see cref="IsError"/> reflects its exit status (a
    /// timeout counts as having run). False when it never started: refused or not confirmed by the
    /// user, or the sandbox itself was unavailable. Only executed commands may turn a build red or green.
    /// </summary>
    public bool CommandExecuted { get; init; }
}

public record AgentStepLog(
    int Step,
    string Action,
    string Details,
    DateTime Timestamp
);
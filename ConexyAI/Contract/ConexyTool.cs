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
);

public record AgentStepLog(
    int Step,
    string Action,
    string Details,
    DateTime Timestamp
);
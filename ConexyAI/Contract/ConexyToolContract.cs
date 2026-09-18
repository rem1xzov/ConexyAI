namespace ConexyAI.Contract;

public record FileReadResult(bool Success, string? Content, string? Error);

public record FileWriteResult(bool Success, string Path, string? Error);

public record FilePatchResult(bool Success, string Path, string? Error, int AppliedOccurrences = 0);

public record FileListResult(bool Success, IReadOnlyList<string> Files, string? Error);

public record CommandExecResult(bool Success, int ExitCode, string StdOut, string StdErr);

public record GitOperationResult(bool Success, string? Message, string? Error, string? PullRequestUrl = null);
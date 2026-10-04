namespace ConexyAI.Contract;

public record FileReadResult(bool Success, string? Content, string? Error);

public record FileWriteResult(bool Success, string Path, string? Error);

// OFFICE_FORMATS: добавлено 2026-09-23 — binary files (.docx/.xlsx/.pptx/.pdf) cannot go through the text API.
public record FileBytesResult(bool Success, byte[]? Content, string? Error);

public record FilePatchResult(bool Success, string Path, string? Error, int AppliedOccurrences = 0);

public record FileListResult(bool Success, IReadOnlyList<string> Files, string? Error);

// GREP_GLOB: добавлено 2026-10-04 — grep/glob как отдельные инструменты агента вместо bash-обёрток.
public record GrepMatch(string Path, int Line, string Text);

public record GrepResult(bool Success, IReadOnlyList<GrepMatch> Matches, int FilesSearched, bool Truncated, string? Error);

public record CommandExecResult(bool Success, int ExitCode, string StdOut, string StdErr);

public record GitOperationResult(bool Success, string? Message, string? Error, string? PullRequestUrl = null);
namespace ConexyAI.Contract;

// IDE_GIT: добавлено 2026-10-04 — Source Control для панели IDE: статус, staging, коммит, ветки, лог.
// Все операции идут локально в рабочей области чата (без push): безопасный, уже закалённый host-git.

/// <summary>One changed path as <c>git status --porcelain</c> reports it.</summary>
public sealed record GitFileChange(
    string Path,
    string IndexStatus,
    string WorkTreeStatus,
    bool Staged)
{
    /// <summary>Single-letter badge for the UI: the worktree status, else the index status.</summary>
    public string Status => WorkTreeStatus != " " ? WorkTreeStatus : IndexStatus;
}

public sealed record GitStatusResult(
    bool Success,
    bool IsRepository,
    string? Branch,
    IReadOnlyList<GitFileChange> Changes,
    string? Error);

public sealed record GitCommitInfo(string Hash, string ShortHash, string Author, string Date, string Subject);

public sealed record GitLogResult(bool Success, IReadOnlyList<GitCommitInfo> Commits, string? Error);

public sealed record GitBranchInfo(string Name, bool IsCurrent);

public sealed record GitBranchesResult(bool Success, IReadOnlyList<GitBranchInfo> Branches, string? Error);

// IDE_GIT: тела запросов Source Control.
public sealed record GitStageRequest(IReadOnlyList<string>? Paths, bool Staged, string? RepoFolder);

public sealed record GitCommitRequest(string? Message, string? AuthorName, string? AuthorEmail, string? RepoFolder);

public sealed record GitCheckoutRequest(string? Branch, string? RepoFolder);

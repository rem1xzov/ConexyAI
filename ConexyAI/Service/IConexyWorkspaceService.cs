using ConexyAI.Contract;

namespace ConexyAI.Service;

public interface IConexyWorkspaceService
{
    // All id parameters below are the chat/thread id (stable per conversation), NOT
    // the per-run task id. One chat = one workspace on disk for its whole lifetime.
    string GetTaskWorkspacePath(Guid chatId);

    /// <summary>Returns the workspace directory for a chat without creating it, or <c>null</c> if it does not exist.</summary>
    string? GetTaskWorkspacePathIfExists(Guid chatId);

    // SANDBOX: добавлено 2026-09-17
    /// <summary>Validates an already-resolved workspace path stays inside the workspaces root (Path Jail).</summary>
    string ValidateWorkspacePath(string fullPath);
    Task<FileReadResult> ReadFileAsync(Guid chatId, string relativePath, CancellationToken ct = default);
    Task<FileWriteResult> WriteFileAsync(Guid chatId, string relativePath, string content, CancellationToken ct = default);
    Task<FileWriteResult> DeleteFileAsync(Guid chatId, string relativePath, CancellationToken ct = default);
    Task<FilePatchResult> PatchFileAsync(Guid chatId, string relativePath, string searchBlock, string replaceBlock, CancellationToken ct = default);
    Task<FileListResult> ListFilesAsync(Guid chatId, string relativeDirectory = "", CancellationToken ct = default);
    Task<CommandExecResult> ExecuteCommandAsync(Guid chatId, string command, string? workingDirectory = null, CancellationToken ct = default);

    /// <summary>Clones a repository into the task workspace using a user-supplied access token.</summary>
    Task<GitOperationResult> GitCloneAsync(Guid chatId, string repoUrl, string token, string? targetFolder = null, string? branch = null, CancellationToken ct = default);

    /// <summary>Creates and switches to a new branch in the workspace repository.</summary>
    Task<GitOperationResult> GitCreateBranchAsync(Guid chatId, string branchName, CancellationToken ct = default);

    /// <summary>Stages the given files, commits them and pushes to the target branch.</summary>
    Task<GitOperationResult> GitCommitPushAsync(
        Guid chatId,
        string commitMessage,
        string branch,
        string token,
        string? repoUrl = null,
        IReadOnlyList<string>? changedFiles = null,
        CancellationToken ct = default);

    /// <summary>Resolves the <c>owner/repo</c> of the workspace repository from the git remote.</summary>
    Task<(string? Owner, string? Repo)> ResolveRepositoryAsync(Guid chatId, string? repoUrl, CancellationToken ct = default);

    /// <summary>Deletes the task sandbox directory (used on fatal failure or on demand).</summary>
    Task CleanupWorkspaceAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>Writes non-graphical attachments into the workspace root with their original names.</summary>
    Task SaveAttachmentsAsync(Guid chatId, IReadOnlyList<TaskAttachment>? attachments, CancellationToken ct = default);

    /// <summary>Returns the persisted run command for a chat, or <c>null</c> if none is stored.</summary>
    Task<string?> GetRunCommandAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>Persists the run command for a chat so it does not need to be re-detected on every run.</summary>
    Task SetRunCommandAsync(Guid chatId, string command, CancellationToken ct = default);
}
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
    // OFFICE_FORMATS: добавлено 2026-09-23 — same path jail as the text methods, for binary documents.
    Task<FileBytesResult> ReadBytesAsync(Guid chatId, string relativePath, CancellationToken ct = default);
    Task<FileWriteResult> WriteBytesAsync(Guid chatId, string relativePath, byte[] content, CancellationToken ct = default);
    Task<FileWriteResult> DeleteFileAsync(Guid chatId, string relativePath, CancellationToken ct = default);
    Task<FilePatchResult> PatchFileAsync(Guid chatId, string relativePath, string searchBlock, string replaceBlock, CancellationToken ct = default);
    Task<FileListResult> ListFilesAsync(Guid chatId, string relativeDirectory = "", CancellationToken ct = default);

    // GREP_GLOB: добавлено 2026-10-04 — поиск по содержимому (grep) и по имени (glob) как отдельные инструменты.
    /// <summary>
    /// Searches file contents under <paramref name="relativeDirectory"/> with a regular expression.
    /// <paramref name="includeGlob"/> optionally restricts which files are searched (e.g. <c>*.ts</c>).
    /// </summary>
    Task<GrepResult> GrepAsync(
        Guid chatId,
        string pattern,
        string? relativeDirectory = null,
        string? includeGlob = null,
        bool ignoreCase = false,
        int maxResults = 0,
        CancellationToken ct = default);

    /// <summary>Finds files matching a glob (supports <c>**</c>) under <paramref name="relativeDirectory"/>.</summary>
    Task<FileListResult> GlobAsync(
        Guid chatId,
        string pattern,
        string? relativeDirectory = null,
        int maxResults = 0,
        CancellationToken ct = default);

    /// <summary>Clones a repository into the task workspace using a user-supplied access token.</summary>
    Task<GitOperationResult> GitCloneAsync(Guid chatId, string repoUrl, string token, string? targetFolder = null, string? branch = null, CancellationToken ct = default);

    /// <summary>Creates and switches to a new branch in the workspace repository.</summary>
    /// <remarks>
    /// GITHUB_REPO_SUBDIR: <paramref name="repoFolder"/> — относительный путь к каталогу репозитория
    /// внутри воркспейса. Если не задан, репозиторий ищется в корне, а затем в единственной подпапке.
    /// </remarks>
    Task<GitOperationResult> GitCreateBranchAsync(Guid chatId, string branchName, string? repoFolder = null, CancellationToken ct = default);

    // GITHUB_FULL: добавлено 2026-10-02 — синхронизация с origin.
    /// <summary>Switches to an EXISTING branch (no create/reset).</summary>
    Task<GitOperationResult> GitSwitchBranchAsync(Guid chatId, string branchName, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Fetches and prunes remote refs (needs the user's token).</summary>
    Task<GitOperationResult> GitFetchAsync(Guid chatId, string token, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Fast-forward pulls the current branch from origin; refuses to create a merge commit.</summary>
    Task<GitOperationResult> GitPullAsync(Guid chatId, string token, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Merges another branch into the current one (no-edit); conflicts are reported, not resolved.</summary>
    Task<GitOperationResult> GitMergeBranchAsync(Guid chatId, string branchName, string? repoFolder = null, CancellationToken ct = default);

    // IDE_GIT: добавлено 2026-10-04 — Source Control для панели IDE (локально, без push).
    /// <summary>Working-tree status of the workspace repository (branch + changed paths).</summary>
    Task<GitStatusResult> GitStatusAsync(Guid chatId, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Stages or unstages paths (empty list = all) in the workspace repository.</summary>
    Task<GitOperationResult> GitStageAsync(Guid chatId, IReadOnlyList<string> paths, bool stage, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Commits the staged changes locally (no push).</summary>
    Task<GitOperationResult> GitCommitAsync(Guid chatId, string message, string? authorName, string? authorEmail, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Recent commits of the current branch, newest first.</summary>
    Task<GitLogResult> GitLogAsync(Guid chatId, int limit = 30, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Local branches with the current one flagged.</summary>
    Task<GitBranchesResult> GitBranchesAsync(Guid chatId, string? repoFolder = null, CancellationToken ct = default);

    // IDE_DIFF: добавлено 2026-10-05 — diff-редактор IDE: содержимое файла в ревизии против рабочей копии.
    /// <summary>Content of one file at a revision (default HEAD) versus its working-tree content.</summary>
    Task<GitFileDiffResult> GitFileDiffAsync(Guid chatId, string path, string? revision = null, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Stages the given files, commits them and pushes to the target branch.</summary>
    /// <remarks>
    /// GITHUB_PAT_PER_USER: <paramref name="authorName"/>/<paramref name="authorEmail"/> — личность
    /// пользователя из его GitHub-токена, чтобы коммит был подписан им, а не общим «Conexy AI Agent».
    /// </remarks>
    Task<GitOperationResult> GitCommitPushAsync(
        Guid chatId,
        string commitMessage,
        string branch,
        string token,
        string? repoUrl = null,
        IReadOnlyList<string>? changedFiles = null,
        string? authorName = null,
        string? authorEmail = null,
        string? repoFolder = null,
        CancellationToken ct = default);

    /// <summary>Deletes a branch locally and, when <paramref name="deleteRemote"/> is set, on origin.</summary>
    /// <remarks>GITHUB_DELETE_BRANCH: добавлено 2026-09-30 — у github_action не было операции удаления ветки.</remarks>
    Task<GitOperationResult> GitDeleteBranchAsync(
        Guid chatId,
        string branchName,
        string? repoFolder,
        bool deleteRemote,
        string token,
        CancellationToken ct = default);

    /// <summary>Resolves the <c>owner/repo</c> of the workspace repository from the git remote.</summary>
    Task<(string? Owner, string? Repo)> ResolveRepositoryAsync(Guid chatId, string? repoUrl, string? repoFolder = null, CancellationToken ct = default);

    /// <summary>Deletes the task sandbox directory (used on fatal failure or on demand).</summary>
    Task CleanupWorkspaceAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// Writes non-graphical attachments into the workspace root with their original names.
    /// Returns the names that could not be written, so the caller can report them instead of
    /// losing the failure silently.
    /// </summary>
    Task<IReadOnlyList<string>> SaveAttachmentsAsync(Guid chatId, IReadOnlyList<TaskAttachment>? attachments, CancellationToken ct = default);

    /// <summary>Returns the persisted run command for a chat, or <c>null</c> if none is stored.</summary>
    Task<string?> GetRunCommandAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>Persists the run command for a chat so it does not need to be re-detected on every run.</summary>
    Task SetRunCommandAsync(Guid chatId, string command, CancellationToken ct = default);
}
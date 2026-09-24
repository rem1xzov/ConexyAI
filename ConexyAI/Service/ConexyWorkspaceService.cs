using System.Diagnostics;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

public class ConexyWorkspaceService : IConexyWorkspaceService
{
    private readonly string _baseWorkspacesDir;
    private readonly ILogger<ConexyWorkspaceService> _logger;
    // WORKSPACE_JAIL: добавлено 2026-09-24 — git-операции на хосте не должны идти параллельно с
    // командой песочницы в том же воркспейсе (та могла бы подменить .git/config на симлинк).
    private readonly ISandboxActivity _sandboxActivity;

    public ConexyWorkspaceService(
        IOptions<WorkspaceOptions> options,
        ILogger<ConexyWorkspaceService> logger,
        ISandboxActivity? sandboxActivity = null)
    {
        _logger = logger;
        _sandboxActivity = sandboxActivity ?? new SandboxActivity();
        var configured = options.Value.RootPath;

        // Isolated storage outside the repository. An empty RootPath falls back to a
        // per-user path under %LOCALAPPDATA% so agent artifacts never enter the source tree.
        var baseDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConexyAI",
                "workspaces")
            : Path.GetFullPath(configured);

        Directory.CreateDirectory(baseDir);
        // WORKSPACE_JAIL: the base itself may legitimately be a symlink (a mounted volume); every
        // containment check compares REAL paths, so the base is stored in its real form.
        _baseWorkspacesDir = WorkspaceJail.GetRealPath(baseDir);
        logger.LogInformation("Agent workspaces root: {WorkspaceRoot}", _baseWorkspacesDir);

        MigrateLegacyWorkspaces(logger);
    }

    /// <summary>Upper bound for <see cref="ReadFileAsync"/>: larger files are refused, not truncated.</summary>
    public const long MaxTextReadBytes = 5L * 1024 * 1024;

    public string GetTaskWorkspacePath(Guid chatId)
    {
        var path = Path.Combine(_baseWorkspacesDir, chatId.ToString("N"));
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    public string? GetTaskWorkspacePathIfExists(Guid chatId)
    {
        var path = Path.Combine(_baseWorkspacesDir, chatId.ToString("N"));
        return Directory.Exists(path) ? path : null;
    }

    public async Task<FileReadResult> ReadFileAsync(Guid chatId, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var resolvedPath = ResolveSafePath(chatId, relativePath);
            if (!File.Exists(resolvedPath))
                return new FileReadResult(false, null, $"File '{relativePath}' not found.");

            // A text read never loads an arbitrarily large file into memory (and into a model prompt).
            var size = new FileInfo(resolvedPath).Length;
            if (size > MaxTextReadBytes)
                return new FileReadResult(false, null, $"File '{relativePath}' is too large to read as text ({size} bytes, limit {MaxTextReadBytes}).");

            var content = await WorkspaceJail.ReadAllTextAsync(GetTaskWorkspacePath(chatId), relativePath, ct);
            return new FileReadResult(true, content, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileReadResult(false, null, ex.Message);
        }
    }

    public async Task<FileWriteResult> WriteFileAsync(Guid chatId, string relativePath, string content, CancellationToken ct = default)
    {
        try
        {
            await WorkspaceJail.WriteAllTextAsync(GetTaskWorkspacePath(chatId), relativePath, content, ct);
            return new FileWriteResult(true, relativePath, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileWriteResult(false, relativePath, ex.Message);
        }
    }

    // OFFICE_FORMATS: добавлено 2026-09-23
    public async Task<FileBytesResult> ReadBytesAsync(Guid chatId, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var resolvedPath = ResolveSafePath(chatId, relativePath);
            if (!File.Exists(resolvedPath))
                return new FileBytesResult(false, null, $"File '{relativePath}' not found.");

            return new FileBytesResult(true, await WorkspaceJail.ReadAllBytesAsync(GetTaskWorkspacePath(chatId), relativePath, ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileBytesResult(false, null, ex.Message);
        }
    }

    // OFFICE_FORMATS: добавлено 2026-09-23
    public async Task<FileWriteResult> WriteBytesAsync(Guid chatId, string relativePath, byte[] content, CancellationToken ct = default)
    {
        try
        {
            await WorkspaceJail.WriteAllBytesAsync(GetTaskWorkspacePath(chatId), relativePath, content, ct);
            return new FileWriteResult(true, relativePath, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileWriteResult(false, relativePath, ex.Message);
        }
    }

    public async Task<FilePatchResult> PatchFileAsync(Guid chatId, string relativePath, string searchBlock, string replaceBlock, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(searchBlock))
                return new FilePatchResult(false, relativePath, "search_block must not be empty.");

            var resolvedPath = ResolveSafePath(chatId, relativePath);
            if (!File.Exists(resolvedPath))
                return new FilePatchResult(false, relativePath, $"File '{relativePath}' not found.");

            var root = GetTaskWorkspacePath(chatId);
            var content = await WorkspaceJail.ReadAllTextAsync(root, relativePath, ct);
            var index = content.IndexOf(searchBlock, StringComparison.Ordinal);
            if (index < 0)
                return new FilePatchResult(false, relativePath, "search_block not found. Patch requires an exact, unambiguous match.");

            // Diff-first safety: reject ambiguous patches instead of corrupting the file.
            var secondIndex = content.IndexOf(searchBlock, index + searchBlock.Length, StringComparison.Ordinal);
            if (secondIndex >= 0)
                return new FilePatchResult(false, relativePath, "search_block matched multiple locations. Include more surrounding context for a unique match.");

            var patched = content.Remove(index, searchBlock.Length).Insert(index, replaceBlock);
            await WorkspaceJail.WriteAllTextAsync(root, relativePath, patched, ct);
            return new FilePatchResult(true, relativePath, null, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FilePatchResult(false, relativePath, ex.Message);
        }
    }

    public Task<FileWriteResult> DeleteFileAsync(Guid chatId, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var resolvedPath = ResolveSafePath(chatId, relativePath);
            if (!File.Exists(resolvedPath))
                return Task.FromResult(new FileWriteResult(false, relativePath, $"File '{relativePath}' not found."));

            File.Delete(resolvedPath);
            return Task.FromResult(new FileWriteResult(true, relativePath, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileWriteResult(false, relativePath, ex.Message));
        }
    }

    public Task<FileListResult> ListFilesAsync(Guid chatId, string relativeDirectory = "", CancellationToken ct = default)
    {
        try
        {
            var root = GetTaskWorkspacePath(chatId);
            var resolvedPath = ResolveSafePath(chatId, relativeDirectory);
            if (!Directory.Exists(resolvedPath))
                return Task.FromResult(new FileListResult(false, Array.Empty<string>(), $"Directory '{relativeDirectory}' not found."));

            // WORKSPACE_JAIL: symlinks are neither listed nor followed.
            var realRoot = WorkspaceJail.GetRealPath(root);
            var files = Directory.EnumerateFiles(resolvedPath, "*", WorkspaceJail.NoLinks(recursive: true))
                .Select(f => Path.GetRelativePath(realRoot, f))
                .ToList();

            return Task.FromResult(new FileListResult(true, files, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileListResult(false, Array.Empty<string>(), ex.Message));
        }
    }

    // WORKSPACE_JAIL: ExecuteCommandAsync удалён 2026-09-24 — он запускал bash прямо на хосте бэкенда
    // в каталоге воркспейса. Вызывающих не осталось (terminal_exec давно идёт через песочницу), а
    // сам метод был готовым способом выполнить что угодно вне Docker.

    public Task CleanupWorkspaceAsync(Guid chatId, CancellationToken ct = default)
    {
        var path = Path.Combine(_baseWorkspacesDir, chatId.ToString("N"));
        if (Directory.Exists(path))
        {
            // Directory.Delete removes symlinks themselves and never recurses into their targets.
            Directory.Delete(path, recursive: true);
        }

        var runConfig = GetRunConfigPath(chatId);
        if (File.Exists(runConfig))
        {
            File.Delete(runConfig);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetRunCommandAsync(Guid chatId, CancellationToken ct = default)
    {
        var path = GetRunConfigPath(chatId);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        try
        {
            var config = JsonSerializer.Deserialize<RunConfigFile>(File.ReadAllText(path));
            return Task.FromResult(string.IsNullOrWhiteSpace(config?.Command) ? null : config.Command);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read run config for chat {ChatId}.", chatId);
            return Task.FromResult<string?>(null);
        }
    }

    public async Task SetRunCommandAsync(Guid chatId, string command, CancellationToken ct = default)
    {
        try
        {
            var path = GetRunConfigPath(chatId);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new RunConfigFile { Command = command.Trim() }), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist run command for chat {ChatId}", chatId);
            throw;
        }
    }

    // The run config is a sibling of the workspace directory (not inside it) so it never
    // appears in the file explorer or the downloaded ZIP, while still being keyed by ChatId
    // exactly like the workspace itself.
    private string GetRunConfigPath(Guid chatId) =>
        Path.Combine(_baseWorkspacesDir, chatId.ToString("N") + ".run.json");

    private sealed class RunConfigFile
    {
        public string Command { get; set; } = string.Empty;
    }

    public async Task<IReadOnlyList<string>> SaveAttachmentsAsync(Guid chatId, IReadOnlyList<TaskAttachment>? attachments, CancellationToken ct = default)
    {
        // ATTACHMENT_ERRORS: добавлено 2026-09-21 — a bad file must not silently vanish: the
        // name is returned so the caller can surface it, and every failure is logged here.
        var failed = new List<string>();
        if (attachments == null || attachments.Count == 0)
        {
            return failed;
        }

        var workspaceDir = GetTaskWorkspacePath(chatId);

        foreach (var attachment in attachments)
        {
            // Images are sent to the LLM context, not materialized on disk.
            if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // GetFileName guards against path traversal in the attachment name.
            var fileName = Path.GetFileName(attachment.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.LogWarning("Skipping attachment with an unusable file name for chat {ChatId}", chatId);
                failed.Add(attachment.FileName ?? "(unnamed)");
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(attachment.ContentBase64?.Trim() ?? string.Empty);
                // WORKSPACE_JAIL: an earlier sandbox command may have left a symlink with this very
                // name; the jail refuses to write through it.
                await WorkspaceJail.WriteAllBytesAsync(workspaceDir, fileName, bytes, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Corrupt base64, an illegal name, a full disk — one bad file must not abort the run.
                _logger.LogError(ex, "Failed to save an attachment for chat {ChatId}", chatId);
                failed.Add(fileName);
            }
        }

        return failed;
    }

    public async Task<GitOperationResult> GitCloneAsync(
        Guid chatId,
        string repoUrl,
        string token,
        string? targetFolder = null,
        string? branch = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return new GitOperationResult(false, null, "Repository URL is required.");

        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        var (owner, repo) = ParseGitHubRepo(repoUrl);
        if (owner is null || repo is null)
            return new GitOperationResult(false, null, "Only GitHub repositories (owner/repo) are supported.");

        if (!string.IsNullOrWhiteSpace(branch) && !IsSafeRefName(branch))
            return new GitOperationResult(false, null, "Invalid branch name.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = GetTaskWorkspacePath(chatId);
        var cloneDir = workspaceDir;

        if (!string.IsNullOrWhiteSpace(targetFolder))
        {
            cloneDir = ResolveSafePath(chatId, targetFolder);
            Directory.CreateDirectory(cloneDir);
        }
        else if (Directory.EnumerateFileSystemEntries(workspaceDir).Any())
        {
            return new GitOperationResult(false, null, "Workspace is not empty. Clone into a fresh workspace.");
        }

        if (Directory.Exists(Path.Combine(cloneDir, ".git")) || File.Exists(Path.Combine(cloneDir, ".git")))
            return new GitOperationResult(false, null, "Target already contains a git repository.");

        // GIT_HARDENING: the token travels as an HTTP header in the process environment, never in the
        // remote URL — so it is not written into .git/config (review M10).
        var args = new List<string> { "clone", "--depth", "1" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.AddRange(new[] { "--branch", branch, "--single-branch" });
        }
        args.AddRange(new[] { "--", PlainRemoteUrl(owner, repo), "." });

        var result = await RunGitAsync(cloneDir, args, token, ct, TimeSpan.FromSeconds(120));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr);

        return new GitOperationResult(true, $"Repository cloned into workspace.", null);
    }

    public async Task<GitOperationResult> GitCreateBranchAsync(Guid chatId, string branchName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, null, "Branch name is required.");
        if (!IsSafeRefName(branchName))
            return new GitOperationResult(false, null, "Invalid branch name.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = GetTaskWorkspacePath(chatId);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var result = await RunGitAsync(workspaceDir, new[] { "checkout", "-B", branchName }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr);

        return new GitOperationResult(true, $"Switched to branch '{branchName}'.", null);
    }

    public async Task<GitOperationResult> GitCommitPushAsync(
        Guid chatId,
        string commitMessage,
        string branch,
        string token,
        string? repoUrl = null,
        IReadOnlyList<string>? changedFiles = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
            return new GitOperationResult(false, null, "Commit message is required.");

        if (string.IsNullOrWhiteSpace(branch))
            return new GitOperationResult(false, null, "Target branch is required.");
        if (!IsSafeRefName(branch))
            return new GitOperationResult(false, null, "Invalid branch name.");

        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = GetTaskWorkspacePath(chatId);

        // Init if the workspace is not yet a repository (agent wrote files without cloning).
        if (!Directory.Exists(Path.Combine(workspaceDir, ".git")) && !File.Exists(Path.Combine(workspaceDir, ".git")))
        {
            var init = await RunGitAsync(workspaceDir, new[] { "init" }, token: null, ct, TimeSpan.FromSeconds(60));
            if (!init.Success) return new GitOperationResult(false, null, init.StdErr);
        }

        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var configName = await RunGitAsync(workspaceDir, new[] { "config", "user.name", "Conexy AI Agent" }, token: null, ct, TimeSpan.FromSeconds(60));
        var configEmail = await RunGitAsync(workspaceDir, new[] { "config", "user.email", "agent@conexy.ai" }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!configName.Success || !configEmail.Success)
            return new GitOperationResult(false, null, "Failed to configure git identity.");

        var (owner, repo) = await ResolveRepositoryCoreAsync(workspaceDir, repoUrl, ct);
        if (owner == null || repo == null)
            return new GitOperationResult(false, null, "Could not determine the target GitHub repository. Pass a repo URL or clone a repository first.");

        // GIT_HARDENING: origin is a plain URL; the token is sent per command (review M10).
        var remoteUrl = PlainRemoteUrl(owner, repo);
        var hasRemote = await RunGitAsync(workspaceDir, new[] { "remote", "get-url", "origin" }, token: null, ct, TimeSpan.FromSeconds(60));
        var remoteArgs = hasRemote.Success
            ? new[] { "remote", "set-url", "origin", remoteUrl }
            : new[] { "remote", "add", "origin", remoteUrl };
        var remote = await RunGitAsync(workspaceDir, remoteArgs, token: null, ct, TimeSpan.FromSeconds(60));
        if (!remote.Success) return new GitOperationResult(false, null, remote.StdErr);

        var checkout = await RunGitAsync(workspaceDir, new[] { "checkout", "-B", branch }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!checkout.Success) return new GitOperationResult(false, null, checkout.StdErr);

        var status = await RunGitAsync(workspaceDir, new[] { "status", "--porcelain" }, token: null, ct, TimeSpan.FromSeconds(60));
        if (string.IsNullOrWhiteSpace(status.StdOut))
            return new GitOperationResult(true, "No changes to commit.", null);

        var addArgs = new List<string> { "add" };
        if (changedFiles is { Count: > 0 })
        {
            addArgs.Add("--");
            addArgs.AddRange(changedFiles);
        }
        else
        {
            addArgs.Add("-A");
        }
        var add = await RunGitAsync(workspaceDir, addArgs, token: null, ct, TimeSpan.FromSeconds(60));
        if (!add.Success) return new GitOperationResult(false, null, add.StdErr);

        var commit = await RunGitAsync(workspaceDir, new[] { "commit", "-m", commitMessage }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!commit.Success) return new GitOperationResult(false, null, commit.StdErr);

        var push = await RunGitAsync(workspaceDir, new[] { "push", "-u", "origin", branch }, token, ct, TimeSpan.FromSeconds(120));
        if (!push.Success) return new GitOperationResult(false, null, push.StdErr);

        return new GitOperationResult(true, "Changes pushed successfully.", null);
    }

    public async Task<(string? Owner, string? Repo)> ResolveRepositoryAsync(Guid chatId, string? repoUrl, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            return ParseGitHubRepo(repoUrl);
        }

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = GetTaskWorkspacePath(chatId);
        if (await PrepareRepositoryAsync(workspaceDir, ct) is not null)
            return (null, null);

        return await ResolveRepositoryCoreAsync(workspaceDir, repoUrl, ct);
    }

    private async Task<(string? Owner, string? Repo)> ResolveRepositoryCoreAsync(string workspaceDir, string? repoUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            return ParseGitHubRepo(repoUrl);
        }

        var remote = await RunGitAsync(workspaceDir, new[] { "remote", "get-url", "origin" }, token: null, ct, TimeSpan.FromSeconds(60));
        return remote.Success && !string.IsNullOrWhiteSpace(remote.StdOut)
            ? ParseGitHubRepo(remote.StdOut.Trim())
            : (null, null);
    }

    // GIT_HARDENING: добавлено 2026-09-24.
    //
    // Git здесь запускается НА ХОСТЕ бэкенда, в каталоге, который агент полностью контролирует изнутри
    // песочницы. Любой из этих файлов превращал «закоммить изменения» в выполнение кода на хосте с его
    // окружением (секреты): .git/hooks/pre-commit, core.fsmonitor (выполняется даже на `git status`),
    // credential.helper, gpg.program, фильтры clean/smudge из .gitattributes, include.path и т.п.
    // Поэтому: хуки выключены, опасные ключи переопределены конфигом уровня команды, а в
    // .git/config остаются только ключи из белого списка; .git и .git/config — только настоящие
    // файлы, не симлинки и не gitdir-указатели.

    private static readonly System.Text.RegularExpressions.Regex AllowedRepoConfigKey = new(
        @"^(core\.(repositoryformatversion|filemode|bare|logallrefupdates|ignorecase|precomposeunicode|symlinks|autocrlf|eol|safecrlf|quotepath|compression|bigfilethreshold)" +
        @"|remote\..+\.(url|fetch|tagopt|prune)" +
        @"|branch\..+\.(remote|merge|rebase)" +
        @"|user\.(name|email)" +
        @"|init\.defaultbranch" +
        @"|extensions\.(objectformat|refstorage)" +
        @"|submodule\..+\.(url|path|active|branch)" +
        @"|(pull\.(rebase|ff)|push\.default|fetch\.prune|merge\.(ff|conflictstyle)|gc\.auto)" +
        @"|(advice|color|i18n)\..+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Makes a workspace repository safe to run host git in. Returns an error message when the layout
    /// itself is unsafe (the operation must not run), or null when it is ready.
    /// </summary>
    private async Task<string?> PrepareRepositoryAsync(string workspaceDir, CancellationToken ct)
    {
        var gitDir = Path.Combine(workspaceDir, ".git");
        if (File.Exists(gitDir) || new DirectoryInfo(gitDir).LinkTarget is not null)
            return "Unsafe repository layout: .git must be a regular directory.";
        if (!Directory.Exists(gitDir))
            return "The workspace is not a git repository.";

        var configPath = Path.Combine(gitDir, "config");
        if (new FileInfo(configPath).LinkTarget is not null || Directory.Exists(configPath))
            return "Unsafe repository layout: .git/config must be a regular file.";
        if (!File.Exists(configPath))
            return null;

        var list = await RunGitAsync(workspaceDir, new[] { "config", "--file", configPath, "--name-only", "--list" }, token: null, ct, TimeSpan.FromSeconds(30));
        if (!list.Success)
            return "Could not read the repository configuration.";

        foreach (var key in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AllowedRepoConfigKey.IsMatch(key))
                continue;

            _logger.LogWarning("Removing non-allowlisted git config key '{Key}' from a workspace repository.", key);
            var unset = await RunGitAsync(workspaceDir, new[] { "config", "--file", configPath, "--unset-all", key }, token: null, ct, TimeSpan.FromSeconds(30));
            if (!unset.Success)
                return "Could not sanitize the repository configuration.";
        }

        // Review M10: older runs stored the token inside remote URLs. Rewrite them credential-free.
        var urls = await RunGitAsync(workspaceDir, new[] { "config", "--file", configPath, "--get-regexp", @"^remote\..*\.url$" }, token: null, ct, TimeSpan.FromSeconds(30));
        foreach (var line in urls.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var key = line[..space];
            var value = line[(space + 1)..];
            if (!value.Contains('@')) continue;

            var (owner, repo) = ParseGitHubRepo(value);
            var replacement = owner is not null && repo is not null ? PlainRemoteUrl(owner, repo) : null;
            var fix = replacement is not null
                ? await RunGitAsync(workspaceDir, new[] { "config", "--file", configPath, key, replacement }, token: null, ct, TimeSpan.FromSeconds(30))
                : await RunGitAsync(workspaceDir, new[] { "config", "--file", configPath, "--unset-all", key }, token: null, ct, TimeSpan.FromSeconds(30));
            if (!fix.Success)
                return "Could not sanitize the repository remote.";
        }

        return null;
    }

    private async Task<CommandExecResult> RunGitAsync(
        string workingDir,
        IReadOnlyList<string> arguments,
        string? token,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // GIT_HARDENING: no system/global config, no prompts, no helpers that could run programs.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_ASKPASS"] = OperatingSystem.IsWindows() ? "cmd /c exit 1" : "false";
        startInfo.Environment["SSH_ASKPASS"] = OperatingSystem.IsWindows() ? "cmd /c exit 1" : "false";
        startInfo.Environment["GIT_EDITOR"] = OperatingSystem.IsWindows() ? "cmd /c exit 0" : "true";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_ALLOW_PROTOCOL"] = "https";

        // Command-scope configuration outranks anything left in the repository's own config.
        var config = new List<(string Key, string Value)>
        {
            ("core.hooksPath", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            ("core.fsmonitor", "false"),
            ("core.pager", "cat"),
            ("credential.helper", string.Empty),
            ("commit.gpgSign", "false"),
            ("tag.gpgSign", "false"),
            ("protocol.allow", "never"),
            ("protocol.https.allow", "always"),
            ("safe.directory", workingDir),
        };
        if (!string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            config.Add(("http.https://github.com/.extraheader", $"AUTHORIZATION: basic {basic}"));
        }

        startInfo.Environment["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            startInfo.Environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            startInfo.Environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
        {
            timeoutCts.CancelAfter(timeout.Value);
        }

        try
        {
            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdOut = Redact(await stdOutTask, token);
            var stdErr = Redact(await stdErrTask, token);

            return new CommandExecResult(
                Success: process.ExitCode == 0,
                ExitCode: process.ExitCode,
                StdOut: stdOut,
                StdErr: stdErr
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
            var seconds = timeout?.TotalSeconds ?? 60;
            return new CommandExecResult(false, -1, string.Empty, $"Command timed out after {seconds} seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CommandExecResult(false, -1, string.Empty, ex.Message);
        }
    }

    private static string Redact(string value, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return value;

        var redacted = value.Replace(secret, "***", StringComparison.Ordinal);
        // Also scrub the URL-encoded form in case the token was escaped in a URL.
        var encoded = Uri.EscapeDataString(secret);
        if (!string.Equals(encoded, secret, StringComparison.Ordinal))
            redacted = redacted.Replace(encoded, "***", StringComparison.Ordinal);

        return redacted;
    }

    private static string PlainRemoteUrl(string owner, string repo) => $"https://github.com/{owner}/{repo}.git";

    private static readonly System.Text.RegularExpressions.Regex GitHubNamePart =
        new(@"^[A-Za-z0-9_.-]{1,100}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static (string? Owner, string? Repo) ParseGitHubRepo(string repoUrl)
    {
        var value = repoUrl.Trim();
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        // Handles: https://github.com/owner/repo(.git), https://user:token@github.com/owner/repo,
        // git@github.com:owner/repo.git, owner/repo
        var path = value;
        var at = path.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            path = path[(at + "github.com".Length)..].TrimStart(':', '/');
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return (null, null);

        var owner = segments[^2];
        var repo = segments[^1];
        if (!GitHubNamePart.IsMatch(owner) || !GitHubNamePart.IsMatch(repo) || owner.StartsWith('-') || repo.StartsWith('-') || repo is "." or "..")
            return (null, null);

        return (owner, repo);
    }

    /// <summary>A branch name that cannot be mistaken for an option or escape the refs namespace.</summary>
    private static bool IsSafeRefName(string name) =>
        name.Length <= 200
        && !name.StartsWith('-')
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9._/-]+$");

    /// <summary>
    /// One-time migration: if a legacy sandbox folder still exists inside the
    /// repository (from before workspaces were isolated), move its sessions into the
    /// new isolated root and remove the old directory from the source tree.
    /// </summary>
    private void MigrateLegacyWorkspaces(ILogger logger)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "conexy_workspaces"),
            Path.Combine(Directory.GetCurrentDirectory(), "conexy_workspaces"),
            Path.Combine(Directory.GetCurrentDirectory(), "ConexyAI", "conexy_workspaces"),
        };

        foreach (var legacy in candidates)
        {
            MigrateLegacyDirectory(legacy, logger);
        }
    }

    private void MigrateLegacyDirectory(string legacy, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(legacy) || !Directory.Exists(legacy))
        {
            return;
        }

        var legacyFull = Path.GetFullPath(legacy);
        var targetFull = Path.GetFullPath(_baseWorkspacesDir);
        if (string.Equals(legacyFull, targetFull, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(legacy))
            {
                var dest = Path.Combine(targetFull, Path.GetFileName(dir));
                if (!Directory.Exists(dest))
                {
                    CopyDirectory(dir, dest);
                }
            }

            Directory.Delete(legacy, recursive: true);
            logger.LogInformation(
                "Migrated legacy workspace directory: {Legacy} -> {Target}",
                legacyFull,
                targetFull);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to migrate legacy workspace directory {Legacy}.", legacyFull);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    // Защита от выхода за пределы рабочей папки задачи (Path Jail).
    // WORKSPACE_JAIL: изменено 2026-09-24 — ревью C2: проверяется РЕАЛЬНЫЙ путь (симлинки разрешены).
    private string ResolveSafePath(Guid chatId, string relativePath) =>
        WorkspaceJail.Resolve(GetTaskWorkspacePath(chatId), relativePath);

    // SANDBOX: добавлено 2026-09-17 — validates an already-resolved workspace path before
    // it is mapped into the Docker sandbox (Path Jail).
    public string ValidateWorkspacePath(string fullPath)
    {
        var resolved = WorkspaceJail.GetRealPath(fullPath);
        if (!WorkspaceJail.IsInside(_baseWorkspacesDir, resolved))
        {
            throw new UnauthorizedAccessException("Path traversal attempt detected: access outside workspace is prohibited.");
        }

        return resolved;
    }
}

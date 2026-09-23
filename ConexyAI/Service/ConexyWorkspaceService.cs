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

    public ConexyWorkspaceService(IOptions<WorkspaceOptions> options, ILogger<ConexyWorkspaceService> logger)
    {
        _logger = logger;
        var configured = options.Value.RootPath;

        // Isolated storage outside the repository. An empty RootPath falls back to a
        // per-user path under %LOCALAPPDATA% so agent artifacts never enter the source tree.
        _baseWorkspacesDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConexyAI",
                "workspaces")
            : Path.GetFullPath(configured);

        Directory.CreateDirectory(_baseWorkspacesDir);
        logger.LogInformation("Agent workspaces root: {WorkspaceRoot}", _baseWorkspacesDir);

        MigrateLegacyWorkspaces(logger);
    }

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

            var content = await File.ReadAllTextAsync(resolvedPath, ct);
            return new FileReadResult(true, content, null);
        }
        catch (Exception ex)
        {
            return new FileReadResult(false, null, ex.Message);
        }
    }

    public async Task<FileWriteResult> WriteFileAsync(Guid chatId, string relativePath, string content, CancellationToken ct = default)
    {
        try
        {
            var resolvedPath = ResolveSafePath(chatId, relativePath);
            var dir = Path.GetDirectoryName(resolvedPath);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(resolvedPath, content, ct);
            return new FileWriteResult(true, relativePath, null);
        }
        catch (Exception ex)
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

            return new FileBytesResult(true, await File.ReadAllBytesAsync(resolvedPath, ct), null);
        }
        catch (Exception ex)
        {
            return new FileBytesResult(false, null, ex.Message);
        }
    }

    // OFFICE_FORMATS: добавлено 2026-09-23
    public async Task<FileWriteResult> WriteBytesAsync(Guid chatId, string relativePath, byte[] content, CancellationToken ct = default)
    {
        try
        {
            var resolvedPath = ResolveSafePath(chatId, relativePath);
            var dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(resolvedPath, content, ct);
            return new FileWriteResult(true, relativePath, null);
        }
        catch (Exception ex)
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

            var content = await File.ReadAllTextAsync(resolvedPath, ct);
            var index = content.IndexOf(searchBlock, StringComparison.Ordinal);
            if (index < 0)
                return new FilePatchResult(false, relativePath, "search_block not found. Patch requires an exact, unambiguous match.");

            // Diff-first safety: reject ambiguous patches instead of corrupting the file.
            var secondIndex = content.IndexOf(searchBlock, index + searchBlock.Length, StringComparison.Ordinal);
            if (secondIndex >= 0)
                return new FilePatchResult(false, relativePath, "search_block matched multiple locations. Include more surrounding context for a unique match.");

            var patched = content.Remove(index, searchBlock.Length).Insert(index, replaceBlock);
            await File.WriteAllTextAsync(resolvedPath, patched, ct);
            return new FilePatchResult(true, relativePath, null, 1);
        }
        catch (Exception ex)
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
            var resolvedPath = ResolveSafePath(chatId, relativeDirectory);
            if (!Directory.Exists(resolvedPath))
                return Task.FromResult(new FileListResult(false, Array.Empty<string>(), $"Directory '{relativeDirectory}' not found."));

            var files = Directory.EnumerateFiles(resolvedPath, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(GetTaskWorkspacePath(chatId), f))
                .ToList();

            return Task.FromResult(new FileListResult(true, files, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileListResult(false, Array.Empty<string>(), ex.Message));
        }
    }

    public async Task<CommandExecResult> ExecuteCommandAsync(Guid chatId, string command, string? workingDirectory = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new CommandExecResult(false, -1, string.Empty, "Command is empty.");

        var dir = string.IsNullOrWhiteSpace(workingDirectory)
            ? GetTaskWorkspacePath(chatId)
            : ResolveSafePath(chatId, workingDirectory);

        return await RunShellAsync(command, dir, secret: null, ct);
    }

    public Task CleanupWorkspaceAsync(Guid chatId, CancellationToken ct = default)
    {
        var path = Path.Combine(_baseWorkspacesDir, chatId.ToString("N"));
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
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
                _logger.LogWarning("Skipping attachment with an unusable file name '{FileName}' for chat {ChatId}", attachment.FileName, chatId);
                failed.Add(attachment.FileName ?? "(unnamed)");
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(attachment.ContentBase64?.Trim() ?? string.Empty);
                await File.WriteAllBytesAsync(Path.Combine(workspaceDir, fileName), bytes, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Corrupt base64, an illegal name, a full disk — one bad file must not abort the run.
                _logger.LogError(ex, "Failed to save attachment '{FileName}' for chat {ChatId}", fileName, chatId);
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

        if (Directory.Exists(Path.Combine(cloneDir, ".git")))
            return new GitOperationResult(false, null, "Target already contains a git repository.");

        var authenticatedUrl = BuildAuthenticatedUrl(repoUrl, token);
        var args = "clone";
        if (!string.IsNullOrWhiteSpace(branch)) args += $" --branch {Quote(branch)} --single-branch";
        args += $" --depth 1 {Quote(authenticatedUrl)} .";

        var result = await RunGitAsync(cloneDir, args, token, ct, TimeSpan.FromSeconds(120));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr);

        return new GitOperationResult(true, $"Repository cloned into workspace.", null);
    }

    public async Task<GitOperationResult> GitCreateBranchAsync(Guid chatId, string branchName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, null, "Branch name is required.");

        var workspaceDir = GetTaskWorkspacePath(chatId);
        var result = await RunGitAsync(workspaceDir, $"checkout -B {Quote(branchName)}", secret: null, ct, TimeSpan.FromSeconds(60));
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

        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        var workspaceDir = GetTaskWorkspacePath(chatId);

        // Init if the workspace is not yet a repository (agent wrote files without cloning).
        if (!Directory.Exists(Path.Combine(workspaceDir, ".git")))
        {
            var init = await RunGitAsync(workspaceDir, "init", secret: null, ct, TimeSpan.FromSeconds(60));
            if (!init.Success) return new GitOperationResult(false, null, init.StdErr);
        }

        var configName = await RunGitAsync(workspaceDir, "config user.name \"Conexy AI Agent\"", secret: null, ct, TimeSpan.FromSeconds(60));
        var configEmail = await RunGitAsync(workspaceDir, "config user.email \"agent@conexy.ai\"", secret: null, ct, TimeSpan.FromSeconds(60));
        if (!configName.Success || !configEmail.Success)
            return new GitOperationResult(false, null, "Failed to configure git identity.");

        var (owner, repo) = await ResolveRepositoryAsync(chatId, repoUrl, ct);
        if (owner == null || repo == null)
            return new GitOperationResult(false, null, "Could not determine the target GitHub repository. Pass a repo URL or clone a repository first.");

        // Point origin at the authenticated URL (refreshes the token on subsequent pushes).
        var authenticatedUrl = BuildAuthenticatedUrl($"{owner}/{repo}", token);
        var hasRemote = await RunGitAsync(workspaceDir, "remote get-url origin", secret: null, ct, TimeSpan.FromSeconds(60));
        var remoteArgs = hasRemote.Success ? $"remote set-url origin {Quote(authenticatedUrl)}" : $"remote add origin {Quote(authenticatedUrl)}";
        var remote = await RunGitAsync(workspaceDir, remoteArgs, secret: token, ct, TimeSpan.FromSeconds(60));
        if (!remote.Success) return new GitOperationResult(false, null, remote.StdErr);

        var checkout = await RunGitAsync(workspaceDir, $"checkout -B {Quote(branch)}", secret: null, ct, TimeSpan.FromSeconds(60));
        if (!checkout.Success) return new GitOperationResult(false, null, checkout.StdErr);

        var status = await RunGitAsync(workspaceDir, "status --porcelain", secret: null, ct, TimeSpan.FromSeconds(60));
        if (string.IsNullOrWhiteSpace(status.StdOut))
            return new GitOperationResult(true, "No changes to commit.", null);

        var addArgs = changedFiles is { Count: > 0 }
            ? $"add -- {string.Join(" ", changedFiles.Select(Quote))}"
            : "add -A";
        var add = await RunGitAsync(workspaceDir, addArgs, secret: null, ct, TimeSpan.FromSeconds(60));
        if (!add.Success) return new GitOperationResult(false, null, add.StdErr);

        var commit = await RunGitAsync(workspaceDir, $"commit -m {Quote(commitMessage)}", secret: null, ct, TimeSpan.FromSeconds(60));
        if (!commit.Success) return new GitOperationResult(false, null, commit.StdErr);

        var push = await RunGitAsync(workspaceDir, $"push -u origin {Quote(branch)}", secret: token, ct, TimeSpan.FromSeconds(120));
        if (!push.Success) return new GitOperationResult(false, null, push.StdErr);

        return new GitOperationResult(true, "Changes pushed successfully.", null);
    }

    public async Task<(string? Owner, string? Repo)> ResolveRepositoryAsync(Guid chatId, string? repoUrl, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            return ParseGitHubRepo(repoUrl);
        }

        var workspaceDir = GetTaskWorkspacePath(chatId);
        var remote = await RunGitAsync(workspaceDir, "remote get-url origin", secret: null, ct, TimeSpan.FromSeconds(60));
        return remote.Success && !string.IsNullOrWhiteSpace(remote.StdOut)
            ? ParseGitHubRepo(remote.StdOut.Trim())
            : (null, null);
    }

    private async Task<CommandExecResult> RunShellAsync(string command, string workingDir, string? secret, CancellationToken ct)
    {
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command {Quote(command)}")
            : ("bash", $"-lc {Quote(command)}");

        return await RunProcessAsync(fileName, args, workingDir, secret, ct, TimeSpan.FromSeconds(60));
    }

    private async Task<CommandExecResult> RunGitAsync(string workingDir, string arguments, string? secret, CancellationToken ct, TimeSpan? timeout = null)
    {
        return await RunProcessAsync("git", arguments, workingDir, secret, ct, timeout);
    }

    private async Task<CommandExecResult> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDir,
        string? secret,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

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

            var stdOut = Redact(await stdOutTask, secret);
            var stdErr = Redact(await stdErrTask, secret);

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
        catch (Exception ex)
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

    // NOTE: The authenticated URL embeds the token so that `git` can authenticate
    // without an interactive prompt. The token never appears in logs or client
    // responses because RunProcessAsync redacts it from stdout/stderr. For a
    // stricter isolation, a credential helper would keep the token out of argv.
    private static string BuildAuthenticatedUrl(string repoUrl, string token)
    {
        var (owner, repo) = ParseGitHubRepo(repoUrl);
        return $"https://x-access-token:{Uri.EscapeDataString(token)}@github.com/{owner}/{repo}.git";
    }

    private static (string? Owner, string? Repo) ParseGitHubRepo(string repoUrl)
    {
        var value = repoUrl.Trim();
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        // Handles: https://github.com/owner/repo(.git), git@github.com:owner/repo.git, owner/repo
        var path = value
            .Replace("git@github.com:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://github.com/", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return (null, null);

        return (segments[^2], segments[^1]);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

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
    private string ResolveSafePath(Guid chatId, string relativePath)
    {
        var root = Path.GetFullPath(GetTaskWorkspacePath(chatId));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        // SANDBOX: добавлено 2026-09-17 — stricter, separator-aware prefix check.
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path traversal attempt detected: access outside workspace is prohibited.");
        }

        return fullPath;
    }

    // SANDBOX: добавлено 2026-09-17 — validates an already-resolved workspace path before
    // it is mapped into the Docker sandbox (Path Jail).
    public string ValidateWorkspacePath(string fullPath)
    {
        var root = Path.GetFullPath(_baseWorkspacesDir);
        var resolved = Path.GetFullPath(fullPath);
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path traversal attempt detected: access outside workspace is prohibited.");
        }

        return resolved;
    }
}

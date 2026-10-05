using System.Diagnostics;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using Microsoft.Extensions.FileSystemGlobbing;
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

    // GREP_GLOB: добавлено 2026-10-04 — отдельные инструменты поиска для агента. Раньше он гонял
    // `bash: grep/rg/find`, что требовало подтверждения команды и зависело от того, что установлено
    // в песочнице. Оба метода используют ту же path-jail и не следуют симлинкам, что и листинг.

    /// <summary>Total bytes scanned by one <see cref="GrepAsync"/> call, so a huge tree cannot stall the run.</summary>
    private const long MaxGrepScanBytes = 32L * 1024 * 1024;

    public Task<GrepResult> GrepAsync(
        Guid chatId,
        string pattern,
        string? relativeDirectory = null,
        string? includeGlob = null,
        bool ignoreCase = false,
        int maxResults = 0,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return Task.FromResult(new GrepResult(false, Array.Empty<GrepMatch>(), 0, false, "pattern must not be empty."));

            System.Text.RegularExpressions.Regex regex;
            try
            {
                regex = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.Compiled |
                    (ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None),
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(new GrepResult(false, Array.Empty<GrepMatch>(), 0, false, "Invalid regular expression: " + ex.Message));
            }

            var root = GetTaskWorkspacePath(chatId);
            var realRoot = WorkspaceJail.GetRealPath(root);
            var baseDir = ResolveSafePath(chatId, relativeDirectory ?? string.Empty);
            if (!Directory.Exists(baseDir))
                return Task.FromResult(new GrepResult(false, Array.Empty<GrepMatch>(), 0, false, $"Directory '{relativeDirectory}' not found."));

            var limit = Math.Clamp(maxResults <= 0 ? 100 : maxResults, 1, 1000);
            var matcher = BuildGlobMatcher(includeGlob, out var globBaseOnly);

            var matches = new List<GrepMatch>();
            var filesSearched = 0;
            var scannedChars = 0L;

            foreach (var file in EnumerateWorkspaceFiles(baseDir))
            {
                ct.ThrowIfCancellationRequested();

                if (matcher is not null && !GlobMatches(matcher, globBaseOnly, ToSlash(Path.GetRelativePath(baseDir, file))))
                    continue;

                var info = new FileInfo(file);
                if (info.Length > MaxTextReadBytes) continue;
                if (scannedChars + info.Length > MaxGrepScanBytes) break;

                string content;
                try { content = File.ReadAllText(file); }
                catch { continue; }
                if (content.IndexOf('\0') >= 0) continue; // binary — пропускаем

                scannedChars += content.Length;
                filesSearched++;

                var relative = ToSlash(Path.GetRelativePath(realRoot, file));
                var lineNumber = 0;
                foreach (var rawLine in content.Split('\n'))
                {
                    lineNumber++;
                    var line = rawLine.TrimEnd('\r');
                    if (!regex.IsMatch(line)) continue;

                    matches.Add(new GrepMatch(relative, lineNumber, line.Length > 400 ? line[..400] + "…" : line));
                    if (matches.Count >= limit)
                        return Task.FromResult(new GrepResult(true, matches, filesSearched, true, null));
                }
            }

            return Task.FromResult(new GrepResult(true, matches, filesSearched, false, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new GrepResult(false, Array.Empty<GrepMatch>(), 0, false, ex.Message));
        }
    }

    public Task<FileListResult> GlobAsync(
        Guid chatId,
        string pattern,
        string? relativeDirectory = null,
        int maxResults = 0,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return Task.FromResult(new FileListResult(false, Array.Empty<string>(), "pattern must not be empty."));

            var root = GetTaskWorkspacePath(chatId);
            var realRoot = WorkspaceJail.GetRealPath(root);
            var baseDir = ResolveSafePath(chatId, relativeDirectory ?? string.Empty);
            if (!Directory.Exists(baseDir))
                return Task.FromResult(new FileListResult(false, Array.Empty<string>(), $"Directory '{relativeDirectory}' not found."));

            var baseReal = WorkspaceJail.GetRealPath(baseDir);
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(ToSlash(pattern.Trim()));
            // Маска без '/' (например '*.ts') матчит имя файла на любом уровне, как в ripgrep.
            var basenameOnly = !pattern.Contains('/');

            var limit = Math.Clamp(maxResults <= 0 ? 200 : maxResults, 1, 2000);
            var files = new List<string>();
            foreach (var file in EnumerateWorkspaceFiles(baseDir))
            {
                ct.ThrowIfCancellationRequested();
                if (!GlobMatches(matcher, basenameOnly, ToSlash(Path.GetRelativePath(baseReal, file)))) continue;
                files.Add(ToSlash(Path.GetRelativePath(realRoot, file)));
                if (files.Count >= limit) break;
            }

            files.Sort(StringComparer.Ordinal);
            return Task.FromResult(new FileListResult(true, files, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new FileListResult(false, Array.Empty<string>(), ex.Message));
        }
    }

    // WORKSPACE_JAIL: symlinks are not followed during a search (review C2).
    private static IEnumerable<string> EnumerateWorkspaceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", WorkspaceJail.NoLinks(recursive: true));

    private static Matcher? BuildGlobMatcher(string? pattern, out bool basenameOnly)
    {
        basenameOnly = pattern is not null && !pattern.Contains('/');
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(ToSlash(pattern.Trim()));
        return matcher;
    }

    private static bool GlobMatches(Matcher matcher, bool basenameOnly, string relativePath) =>
        matcher.Match(basenameOnly ? Path.GetFileName(relativePath) : relativePath).HasMatches;

    private static string ToSlash(string path) => path.Replace('\\', '/');

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
            // VIEW_IMAGE: добавлено 2026-10-04 — картинки тоже материализуются на диск (в корень
            // рабочей области), чтобы агент мог пересматривать их через view_image и в следующих
            // ходах, когда исходный блок image_url уже ушёл из контекста.

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

    // GITHUB_REPO_SUBDIR: добавлено 2026-09-30.
    //
    // Агент обычно клонирует репозиторий В ПОДПАПКУ воркспейса (clone_repo с target_folder,
    // например "ConexyAI"), а ветка и коммит работали жёстко в корне воркспейса. В корне .git нет,
    // и инструмент отвечал «The workspace is not a git repository», хотя репозиторий лежал рядом.
    // Теперь каталог репозитория определяется так: явный repo_folder → корень, если это репозиторий →
    // единственная подпапка верхнего уровня с .git. Если репозиториев несколько или ни одного —
    // возвращается корень, и внятную ошибку выдаёт PrepareRepositoryAsync.
    private string ResolveRepositoryDirectory(Guid chatId, string? repoFolder)
    {
        var workspaceDir = GetTaskWorkspacePath(chatId);

        if (!string.IsNullOrWhiteSpace(repoFolder))
            return ResolveSafePath(chatId, repoFolder);

        if (IsRepositoryDirectory(workspaceDir))
            return workspaceDir;

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(workspaceDir).Where(IsRepositoryDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return workspaceDir;
        }

        return candidates.Count == 1 ? candidates[0] : workspaceDir;
    }

    private static bool IsRepositoryDirectory(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

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

    public async Task<GitOperationResult> GitCreateBranchAsync(Guid chatId, string branchName, string? repoFolder = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, null, "Branch name is required.");
        if (!IsSafeRefName(branchName))
            return new GitOperationResult(false, null, "Invalid branch name.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var result = await RunGitAsync(workspaceDir, new[] { "checkout", "-B", branchName }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr);

        return new GitOperationResult(true, $"Switched to branch '{branchName}'.", null);
    }

    // GITHUB_FULL: добавлено 2026-10-02 — переключение на СУЩЕСТВУЮЩУЮ ветку (без создания/сброса).
    public async Task<GitOperationResult> GitSwitchBranchAsync(Guid chatId, string branchName, string? repoFolder = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, null, "Branch name is required.");
        if (!IsSafeRefName(branchName))
            return new GitOperationResult(false, null, "Invalid branch name.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var result = await RunGitAsync(workspaceDir, new[] { "checkout", branchName }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr);

        return new GitOperationResult(true, $"Switched to branch '{branchName}'.", null);
    }

    public async Task<GitOperationResult> GitFetchAsync(Guid chatId, string token, string? repoFolder = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var result = await RunGitAsync(workspaceDir, new[] { "fetch", "--prune", "origin" }, token, ct, TimeSpan.FromSeconds(120));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr);

        return new GitOperationResult(true, "Fetched from origin (pruned).", null);
    }

    public async Task<GitOperationResult> GitPullAsync(Guid chatId, string token, string? repoFolder = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var current = await RunGitAsync(workspaceDir, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, token: null, ct, TimeSpan.FromSeconds(30));
        var branch = current.Success ? current.StdOut.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD")
            return new GitOperationResult(false, null, "Could not determine the current branch to pull.");

        // Только fast-forward: расхождение ветки и origin не должно превращаться в неожиданный merge.
        var result = await RunGitAsync(workspaceDir, new[] { "pull", "--ff-only", "origin", branch }, token, ct, TimeSpan.FromSeconds(120));
        if (!result.Success)
            return new GitOperationResult(false, null, result.StdErr +
                $"\n(Тяну только fast-forward. Если ветки разошлись — посмотри origin/{branch} и влей нужное через merge_branch.)");

        return new GitOperationResult(true, $"Pulled origin/{branch} (fast-forward).", null);
    }

    public async Task<GitOperationResult> GitMergeBranchAsync(Guid chatId, string branchName, string? repoFolder = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, null, "Branch name is required.");
        if (!IsSafeRefName(branchName))
            return new GitOperationResult(false, null, "Invalid branch name.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        // --no-edit: слияние не должно открывать редактор. Конфликты слияние НЕ разрешает — о них
        // сообщаем текстом, чтобы модель поправила файлы и завершила слияние коммитом.
        var result = await RunGitAsync(workspaceDir, new[] { "merge", "--no-edit", branchName }, token: null, ct, TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new GitOperationResult(false, null,
                (result.StdOut + "\n" + result.StdErr).Trim() +
                "\n(Возможно, конфликт: разреши его в файлах, затем закоммить слияние через commit_and_push.)");

        var summary = result.StdOut.Trim();
        return new GitOperationResult(true,
            (string.IsNullOrEmpty(summary) ? string.Empty : summary + "\n") + $"Merged '{branchName}' into the current branch.",
            null);
    }

    // IDE_GIT: добавлено 2026-10-04 — Source Control для панели IDE. Всё локально (без push), через тот же
    // закалённый host-git, что и остальные операции агента.
    public async Task<GitStatusResult> GitStatusAsync(Guid chatId, string? repoFolder = null, CancellationToken ct = default)
    {
        // Reading never creates the workspace: a brand-new chat simply has no repository yet.
        if (GetTaskWorkspacePathIfExists(chatId) is null)
            return new GitStatusResult(true, false, null, Array.Empty<GitFileChange>(), null);

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitStatusResult(false, false, null, Array.Empty<GitFileChange>(), unsafeRepo);

        var branchResult = await RunGitAsync(workspaceDir, new[] { "branch", "--show-current" }, null, ct, TimeSpan.FromSeconds(30));
        var branch = branchResult.Success ? branchResult.StdOut.Trim() : string.Empty;

        var statusResult = await RunGitAsync(workspaceDir,
            new[] { "-c", "core.quotepath=false", "status", "--porcelain=v1", "--untracked-files=all" }, null, ct, TimeSpan.FromSeconds(30));
        if (!statusResult.Success)
            return new GitStatusResult(false, true, NullIfEmpty(branch), Array.Empty<GitFileChange>(), statusResult.StdErr);

        var changes = new List<GitFileChange>();
        foreach (var raw in statusResult.StdOut.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length < 4) continue;
            var index = raw[0].ToString();
            var work = raw[1].ToString();
            var path = raw[3..];
            if (path.Length == 0) continue;
            var staged = index != " " && index != "?";
            changes.Add(new GitFileChange(path, index, work, staged));
        }

        return new GitStatusResult(true, true, NullIfEmpty(branch), changes, null);
    }

    public async Task<GitOperationResult> GitStageAsync(Guid chatId, IReadOnlyList<string> paths, bool stage, string? repoFolder = null, CancellationToken ct = default)
    {
        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var args = new List<string>();
        if (paths is null || paths.Count == 0)
        {
            args.AddRange(stage ? new[] { "add", "-A" } : new[] { "reset", "-q" });
        }
        else
        {
            var safe = new List<string>();
            foreach (var path in paths)
            {
                var normalized = NormalizeRepoPath(path);
                if (normalized is null)
                    return new GitOperationResult(false, null, $"Invalid path '{path}'.");
                safe.Add(normalized);
            }

            if (stage)
            {
                args.Add("add");
                args.Add("--");
                args.AddRange(safe);
            }
            else
            {
                args.Add("reset");
                args.Add("-q");
                args.Add("HEAD");
                args.Add("--");
                args.AddRange(safe);
            }
        }

        var result = await RunGitAsync(workspaceDir, args, token: null, ct, TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new GitOperationResult(false, null, string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr);
        return new GitOperationResult(true, stage ? "Staged." : "Unstaged.", null);
    }

    public async Task<GitOperationResult> GitCommitAsync(Guid chatId, string message, string? authorName, string? authorEmail, string? repoFolder = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitOperationResult(false, null, "Commit message is required.");

        var clean = message.Replace("\0", string.Empty).Trim();
        if (clean.Length > 5000) clean = clean[..5000];

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        var name = string.IsNullOrWhiteSpace(authorName) ? "Conexy AI" : authorName.Trim();
        var email = string.IsNullOrWhiteSpace(authorEmail) ? "agent@conexy.ai" : authorEmail.Trim();
        var args = new List<string>
        {
            "-c", $"user.name={name}",
            "-c", $"user.email={email}",
            "commit", "-m", clean
        };

        var result = await RunGitAsync(workspaceDir, args, token: null, ct, TimeSpan.FromSeconds(60));
        if (!result.Success)
            return new GitOperationResult(false, null, string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr);

        var summary = result.StdOut.Trim();
        return new GitOperationResult(true, string.IsNullOrEmpty(summary) ? "Committed." : summary, null);
    }

    public async Task<GitLogResult> GitLogAsync(Guid chatId, int limit = 30, string? repoFolder = null, CancellationToken ct = default)
    {
        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitLogResult(false, Array.Empty<GitCommitInfo>(), unsafeRepo);

        var take = Math.Clamp(limit <= 0 ? 30 : limit, 1, 200);
        var args = new[] { "-c", "core.quotepath=false", "log", "--pretty=format:%H%x1f%an%x1f%ad%x1f%s", "--date=short", "-n", take.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var result = await RunGitAsync(workspaceDir, args, token: null, ct, TimeSpan.FromSeconds(30));
        if (!result.Success)
        {
            // Unborn HEAD (a repository with no commits yet) is not an error for the UI.
            if ((result.StdErr ?? string.Empty).Contains("does not have any commits", StringComparison.OrdinalIgnoreCase))
                return new GitLogResult(true, Array.Empty<GitCommitInfo>(), null);
            return new GitLogResult(false, Array.Empty<GitCommitInfo>(), result.StdErr);
        }

        var commits = new List<GitCommitInfo>();
        foreach (var line in result.StdOut.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\u001f');
            if (parts.Length < 4) continue;
            var hash = parts[0];
            commits.Add(new GitCommitInfo(hash, hash.Length > 7 ? hash[..7] : hash, parts[1], parts[2], parts[3]));
        }

        return new GitLogResult(true, commits, null);
    }

    public async Task<GitBranchesResult> GitBranchesAsync(Guid chatId, string? repoFolder = null, CancellationToken ct = default)
    {
        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitBranchesResult(false, Array.Empty<GitBranchInfo>(), unsafeRepo);

        var currentResult = await RunGitAsync(workspaceDir, new[] { "branch", "--show-current" }, null, ct, TimeSpan.FromSeconds(30));
        var current = currentResult.Success ? currentResult.StdOut.Trim() : string.Empty;

        var listResult = await RunGitAsync(workspaceDir,
            new[] { "-c", "core.quotepath=false", "for-each-ref", "--format=%(refname:short)", "refs/heads" }, null, ct, TimeSpan.FromSeconds(30));
        if (!listResult.Success)
            return new GitBranchesResult(false, Array.Empty<GitBranchInfo>(), listResult.StdErr);

        var branches = new List<GitBranchInfo>();
        foreach (var raw in listResult.StdOut.Replace("\r\n", "\n").Split('\n'))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            branches.Add(new GitBranchInfo(name, string.Equals(name, current, StringComparison.Ordinal)));
        }

        return new GitBranchesResult(true, branches, null);
    }

    // IDE_DIFF: добавлено 2026-10-05 — «до/после» для diff-редактора: версия файла в ревизии
    // (по умолчанию HEAD) и его текущее содержимое в рабочей копии.
    private const int MaxDiffBytes = 2_000_000;

    public async Task<GitFileDiffResult> GitFileDiffAsync(Guid chatId, string path, string? revision = null, string? repoFolder = null, CancellationToken ct = default)
    {
        var rev = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision.Trim();
        var normalized = NormalizeRepoPath(path);
        if (normalized is null)
            return new GitFileDiffResult(false, path ?? string.Empty, rev, false, false, false, string.Empty, string.Empty, "Invalid path.");
        if (!IsSafeRefName(rev))
            return new GitFileDiffResult(false, normalized, rev, false, false, false, string.Empty, string.Empty, "Invalid revision.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitFileDiffResult(false, normalized, rev, false, false, false, string.Empty, string.Empty, unsafeRepo);

        // Original side: the blob at the requested revision. Absent (new file / unborn HEAD) is not fatal.
        var show = await RunGitAsync(workspaceDir, new[] { "show", $"{rev}:{normalized}" }, null, ct, TimeSpan.FromSeconds(30));
        var hasOriginal = show.Success;
        var original = show.Success ? show.StdOut : string.Empty;

        // Modified side: the working-tree file, read through the workspace jail.
        var fullPath = Path.Combine(workspaceDir, normalized.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            ValidateWorkspacePath(fullPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new GitFileDiffResult(false, normalized, rev, false, false, false, string.Empty, string.Empty, "Invalid path.");
        }

        var hasModified = false;
        var modified = string.Empty;
        try
        {
            if (File.Exists(fullPath))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                hasModified = true;
                modified = System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
        catch (IOException)
        {
            // Unreadable file: report it as absent rather than failing the whole diff.
        }

        // Binary (NUL byte) or oversized files are not diffable in the editor.
        var binary = original.Contains('\0') || modified.Contains('\0')
                     || original.Length > MaxDiffBytes || modified.Length > MaxDiffBytes;
        if (binary)
            return new GitFileDiffResult(true, normalized, rev, hasOriginal, hasModified, true, string.Empty, string.Empty, null);

        return new GitFileDiffResult(true, normalized, rev, hasOriginal, hasModified, false, original, modified, null);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // IDE_GIT: pathspec safety — no absolute paths, no parent traversal, never an option-looking value.
    private static string? NormalizeRepoPath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.StartsWith('-')) return null;
        if (normalized.Length >= 2 && normalized[1] == ':') return null;
        if (normalized.Contains('\0')) return null;
        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..") return null;
        }
        return normalized;
    }

    public async Task<GitOperationResult> GitCommitPushAsync(
        Guid chatId,
        string commitMessage,
        string branch,
        string token,
        string? repoUrl = null,
        IReadOnlyList<string>? changedFiles = null,
        string? authorName = null,
        string? authorEmail = null,
        string? repoFolder = null,
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

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);

        // Init if the target directory is not yet a repository (agent wrote files without cloning).
        if (!Directory.Exists(Path.Combine(workspaceDir, ".git")) && !File.Exists(Path.Combine(workspaceDir, ".git")))
        {
            Directory.CreateDirectory(workspaceDir);
            var init = await RunGitAsync(workspaceDir, new[] { "init" }, token: null, ct, TimeSpan.FromSeconds(60));
            if (!init.Success) return new GitOperationResult(false, null, init.StdErr);
        }

        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        // GITHUB_PAT_PER_USER: личность коммита берём из GitHub-аккаунта пользователя, чей токен
        // реально пушит. Раньше здесь жёстко стоял «Conexy AI Agent», поэтому все коммиты всех
        // пользователей выглядели как коммиты одного служебного аккаунта.
        var commitAuthor = string.IsNullOrWhiteSpace(authorName) ? "Conexy AI Agent" : authorName;
        var commitEmail = string.IsNullOrWhiteSpace(authorEmail) ? "agent@conexy.ai" : authorEmail;
        var configName = await RunGitAsync(workspaceDir, new[] { "config", "user.name", commitAuthor }, token: null, ct, TimeSpan.FromSeconds(60));
        var configEmail = await RunGitAsync(workspaceDir, new[] { "config", "user.email", commitEmail }, token: null, ct, TimeSpan.FromSeconds(60));
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
            return new GitOperationResult(true, "No changes to commit: the working tree is clean, nothing was pushed. Create or edit files first.", null);

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

    // GITHUB_DELETE_BRANCH: добавлено 2026-09-30. У github_action не было операции удаления ветки,
    // хотя пользователи её просят («создай ветку, потом удали на GitHub»), и агент вместо честного
    // отказа выдумывал успех. Удаляет ветку на origin (по флагу) и локально.
    public async Task<GitOperationResult> GitDeleteBranchAsync(
        Guid chatId,
        string branchName,
        string? repoFolder,
        bool deleteRemote,
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return new GitOperationResult(false, null, "Branch name is required.");
        if (!IsSafeRefName(branchName))
            return new GitOperationResult(false, null, "Invalid branch name.");

        // Удаление основной ветки — слишком легко потерять работу; отказываем всегда.
        var trimmed = branchName.Trim();
        if (trimmed is "main" or "master" or "HEAD" || trimmed.StartsWith("origin/", StringComparison.Ordinal))
            return new GitOperationResult(false, null, $"Refusing to delete the protected branch '{trimmed}'.");

        if (deleteRemote && string.IsNullOrWhiteSpace(token))
            return new GitOperationResult(false, null, "GitHub token is required to delete a remote branch.");

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);

        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
        var unsafeRepo = await PrepareRepositoryAsync(workspaceDir, ct);
        if (unsafeRepo is not null)
            return new GitOperationResult(false, null, unsafeRepo);

        // Локально нельзя удалить ветку, на которой стоим, — сначала уходим на базовую.
        var current = await RunGitAsync(workspaceDir, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, token: null, ct, TimeSpan.FromSeconds(30));
        if (current.Success && string.Equals(current.StdOut.Trim(), branchName, StringComparison.Ordinal))
        {
            var baseBranch = await ResolveDefaultBranchAsync(workspaceDir, ct);
            var checkout = await RunGitAsync(workspaceDir, new[] { "checkout", baseBranch }, token: null, ct, TimeSpan.FromSeconds(60));
            if (!checkout.Success)
                return new GitOperationResult(false, null, $"Cannot delete the current branch: failed to switch to '{baseBranch}'. {checkout.StdErr}");
        }

        var messages = new List<string>();

        if (deleteRemote)
        {
            var remote = await RunGitAsync(workspaceDir, new[] { "push", "origin", "--delete", branchName }, token, ct, TimeSpan.FromSeconds(120));
            if (!remote.Success)
                return new GitOperationResult(false, null, remote.StdErr);
            messages.Add($"Deleted remote branch '{branchName}'.");
        }

        // Локальная ветка может отсутствовать (её не создавали в этом воркспейсе) — это не ошибка.
        var local = await RunGitAsync(workspaceDir, new[] { "branch", "-D", branchName }, token: null, ct, TimeSpan.FromSeconds(60));
        if (local.Success)
            messages.Add($"Deleted local branch '{branchName}'.");

        return new GitOperationResult(
            true,
            messages.Count > 0 ? string.Join(" ", messages) : $"Branch '{branchName}' was not present locally or on origin.",
            null);
    }

    /// <summary>Имя базовой ветки репозитория (origin/HEAD), с фолбэком на main/master.</summary>
    private async Task<string> ResolveDefaultBranchAsync(string workspaceDir, CancellationToken ct)
    {
        var head = await RunGitAsync(workspaceDir, new[] { "symbolic-ref", "--short", "refs/remotes/origin/HEAD" }, token: null, ct, TimeSpan.FromSeconds(30));
        var name = head.Success ? head.StdOut.Trim() : string.Empty;
        if (name.StartsWith("origin/", StringComparison.Ordinal))
            name = name["origin/".Length..];
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        foreach (var candidate in new[] { "main", "master" })
        {
            var exists = await RunGitAsync(workspaceDir, new[] { "rev-parse", "--verify", candidate }, token: null, ct, TimeSpan.FromSeconds(30));
            if (exists.Success)
                return candidate;
        }

        return "main";
    }

    public async Task<(string? Owner, string? Repo)> ResolveRepositoryAsync(Guid chatId, string? repoUrl, string? repoFolder = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            return ParseGitHubRepo(repoUrl);
        }

        using var slot = await _sandboxActivity.AcquireAsync(chatId, ct);
        var workspaceDir = ResolveRepositoryDirectory(chatId, repoFolder);
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
            CreateNoWindow = true,
            // Git writes UTF-8; pin the decoding so Cyrillic file content and commit messages survive.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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

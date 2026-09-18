using System.Text;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service;

public interface IIdeFileService
{
    Task<IReadOnlyList<FileNode>> ListAsync(Guid sessionId, string path, CancellationToken ct = default);
    Task<FileContentResponse> GetContentAsync(Guid sessionId, string path, CancellationToken ct = default);
    Task SaveAsync(Guid sessionId, string path, string content, CancellationToken ct = default);
    Task CreateAsync(Guid sessionId, string path, bool isDirectory, CancellationToken ct = default);
    Task DeleteAsync(Guid sessionId, string path, CancellationToken ct = default);
    Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken ct = default);
}

/// <summary>
/// File-explorer CRUD used by the UI. Reuses the shared path validator, undo state and
/// per-path write locks so manual edits interoperate safely with the agent's
/// <see cref="ConexyEditorService"/>. Every mutation broadcasts a <c>ToolActionEvent</c>
/// with <c>ToolName = "user"</c> and invalidates the agent's undo stack for the touched file.
/// </summary>
public class IdeFileService : IIdeFileService
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly IWorkspacePathValidator _pathValidator;
    private readonly IConexyEditorStateService _editorState;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<IdeFileService> _logger;

    public IdeFileService(
        IConexyWorkspaceService workspaceService,
        IWorkspacePathValidator pathValidator,
        IConexyEditorStateService editorState,
        IHubContext<ConexyHub> hubContext,
        ILogger<IdeFileService> logger)
    {
        _workspaceService = workspaceService;
        _pathValidator = pathValidator;
        _editorState = editorState;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task<IReadOnlyList<FileNode>> ListAsync(Guid sessionId, string path, CancellationToken ct = default)
    {
        var fullPath = ResolveDirectory(sessionId, path);
        var root = _workspaceService.GetTaskWorkspacePath(sessionId);

        var nodes = new List<FileNode>();
        foreach (var dir in Directory.EnumerateDirectories(fullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new FileNode
            {
                Name = Path.GetFileName(dir),
                Path = Relative(root, dir),
                IsDirectory = true
            });
        }

        foreach (var file in Directory.EnumerateFiles(fullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            nodes.Add(new FileNode
            {
                Name = info.Name,
                Path = Relative(root, file),
                IsDirectory = false,
                SizeBytes = info.Length,
                ModifiedAt = info.LastWriteTimeUtc
            });
        }

        return Task.FromResult<IReadOnlyList<FileNode>>(nodes);
    }

    public async Task<FileContentResponse> GetContentAsync(Guid sessionId, string path, CancellationToken ct = default)
    {
        var fullPath = ResolveFile(sessionId, path);
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);

        if (WorkspaceFileSafety.IsBinary(bytes))
        {
            return new FileContentResponse { Path = path, Content = string.Empty, IsBinary = true };
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (bytes.Length > WorkspaceFileSafety.MaxViewBytes)
        {
            var preview = text[..Math.Min(text.Length, WorkspaceFileSafety.MaxPreviewChars)];
            text = preview + $"\n... (file truncated: {bytes.Length} bytes)";
        }

        return new FileContentResponse { Path = path, Content = text, IsBinary = false };
    }

    public Task SaveAsync(Guid sessionId, string path, string content, CancellationToken ct = default) =>
        _editorState.WithLockAsync(sessionId, path, async () =>
        {
            var fullPath = _pathValidator.ResolveSafePath(sessionId, path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content ?? string.Empty, ct);

            // A manual user save supersedes the agent's undo history for this file.
            _editorState.ClearUndo(sessionId, path);
            await SendUserActionAsync(sessionId, path, "save", $"Сохранён файл {Path.GetFileName(path)}", ct);
        });

    public Task CreateAsync(Guid sessionId, string path, bool isDirectory, CancellationToken ct = default) =>
        _editorState.WithLockAsync(sessionId, path, async () =>
        {
            var fullPath = _pathValidator.ResolveSafePath(sessionId, path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                throw new InvalidOperationException($"'{path}' already exists.");

            if (isDirectory)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(fullPath, string.Empty, ct);
            }

            await SendUserActionAsync(sessionId, path, "create", isDirectory ? $"Создана директория {Path.GetFileName(path)}" : $"Создан файл {Path.GetFileName(path)}", ct);
        });

    public Task DeleteAsync(Guid sessionId, string path, CancellationToken ct = default) =>
        _editorState.WithLockAsync(sessionId, path, async () =>
        {
            var fullPath = _pathValidator.ResolveSafePath(sessionId, path);
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            else
            {
                throw new FileNotFoundException($"'{path}' not found.");
            }

            _editorState.ClearUndo(sessionId, path);
            await SendUserActionAsync(sessionId, path, "delete", $"Удалён {Path.GetFileName(path)}", ct);
        });

    public Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken ct = default) =>
        _editorState.WithLockAsync(sessionId, oldPath, async () =>
        {
            var fullOld = _pathValidator.ResolveSafePath(sessionId, oldPath);
            var fullNew = _pathValidator.ResolveSafePath(sessionId, newPath);

            if (!File.Exists(fullOld) && !Directory.Exists(fullOld))
                throw new FileNotFoundException($"'{oldPath}' not found.");
            if (File.Exists(fullNew) || Directory.Exists(fullNew))
                throw new InvalidOperationException($"'{newPath}' already exists.");

            var newDir = Path.GetDirectoryName(fullNew);
            if (!string.IsNullOrEmpty(newDir)) Directory.CreateDirectory(newDir);

            if (Directory.Exists(fullOld))
                Directory.Move(fullOld, fullNew);
            else
                File.Move(fullOld, fullNew);

            _editorState.ClearUndo(sessionId, oldPath);
            _editorState.ClearUndo(sessionId, newPath);
            await SendUserActionAsync(sessionId, oldPath, "rename", $"Переименован в {Path.GetFileName(newPath)}", ct);
        });

    private string ResolveDirectory(Guid sessionId, string path)
    {
        var fullPath = _pathValidator.ResolveSafePath(sessionId, string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"'{path}' not found.");
        return fullPath;
    }

    private string ResolveFile(Guid sessionId, string path)
    {
        var fullPath = _pathValidator.ResolveSafePath(sessionId, path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"'{path}' not found.");
        return fullPath;
    }

    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private async Task SendUserActionAsync(Guid sessionId, string path, string command, string summary, CancellationToken ct)
    {
        var evt = new ToolActionEvent
        {
            ToolName = "user",
            Command = command,
            Path = path,
            Status = "completed",
            Summary = summary
        };
        await _hubContext.Clients.Group($"task_{sessionId}").SendAsync("ToolAction", evt, ct);
    }
}

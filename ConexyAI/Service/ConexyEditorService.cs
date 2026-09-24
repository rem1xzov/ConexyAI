using System.IO;
using System.Text;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

public interface IConexyEditorService
{
    Task<StrReplaceEditorResult> ExecuteAsync(Guid taskId, StrReplaceEditorRequest request, CancellationToken ct = default);
}

/// <summary>
/// Implements the <c>str_replace_editor</c> tool: view / create / str_replace / insert / undo
/// with per-session undo stacks, path-traversal protection and SignalR live-action events.
/// Registered as Scoped; undo stacks and path locks live in the shared
/// <see cref="IConexyEditorStateService"/> so manual user edits can invalidate them.
/// </summary>
public class ConexyEditorService : IConexyEditorService
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly IConexyEditorStateService _editorState;
    private readonly IWorkspacePathValidator _pathValidator;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<ConexyEditorService> _logger;

    private static readonly string[] IgnoredDirectories = { "node_modules", "bin", "obj", ".git" };

    public ConexyEditorService(
        IConexyWorkspaceService workspaceService,
        IConexyEditorStateService editorState,
        IWorkspacePathValidator pathValidator,
        IHubContext<ConexyHub> hubContext,
        ILogger<ConexyEditorService> logger)
    {
        _workspaceService = workspaceService;
        _editorState = editorState;
        _pathValidator = pathValidator;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<StrReplaceEditorResult> ExecuteAsync(Guid taskId, StrReplaceEditorRequest request, CancellationToken ct = default)
    {
        var command = (request.Command ?? string.Empty).ToLowerInvariant();

        await SendToolActionAsync(taskId, request.Path, command, "started", null, ct);

        StrReplaceEditorResult result;
        try
        {
            result = await ExecuteCoreAsync(taskId, request, command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "str_replace_editor '{Command}' failed for {Path}", command, request.Path);
            result = Fail("io_error", ex.Message);
        }

        await SendToolActionAsync(
            taskId,
            request.Path,
            command,
            result.Success ? "completed" : "failed",
            result.Success ? BuildSummary(command, request.Path) : result.ErrorDetail,
            ct);

        return result;
    }

    private Task<StrReplaceEditorResult> ExecuteCoreAsync(Guid taskId, StrReplaceEditorRequest request, string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return Task.FromResult(Fail("io_error", "str_replace_editor requires a non-empty 'path'."));

        return command switch
        {
            "view" => ViewAsync(taskId, request.Path, request.ViewRange, ct),
            "create" => WithLockAsync(taskId, request.Path, () => CreateAsync(taskId, request.Path, request.FileText, ct)),
            "str_replace" => WithLockAsync(taskId, request.Path, () => StrReplaceAsync(taskId, request.Path, request.OldStr, request.NewStr, ct)),
            "insert" => WithLockAsync(taskId, request.Path, () => InsertAsync(taskId, request.Path, request.NewStr, request.InsertLine, ct)),
            "undo" => WithLockAsync(taskId, request.Path, () => Task.FromResult(Undo(taskId, request.Path))),
            _ => Task.FromResult(Fail("io_error", $"Unknown command '{command}'."))
        };
    }

    private async Task<StrReplaceEditorResult> ViewAsync(Guid taskId, string path, int[]? viewRange, CancellationToken ct)
    {
        var fullPath = ResolvePath(taskId, path);

        if (Directory.Exists(fullPath))
            return Ok(ListDirectory(fullPath));

        if (!File.Exists(fullPath))
            return Fail("file_not_found", $"File '{path}' not found.");

        var bytes = await WorkspaceJail.ReadAllBytesAsync(Root(taskId), fullPath, ct);
        if (IsBinary(bytes))
            return Fail("io_error", $"File '{path}' appears to be binary and cannot be displayed as text.");

        var text = Encoding.UTF8.GetString(bytes);
        var lines = SplitLines(text);

        if (viewRange is { Length: >= 2 })
        {
            var start = viewRange[0];
            var end = viewRange[1];
            if (start < 1 || (end != -1 && end < start))
                return Fail("invalid_range", $"Invalid view_range [{start}, {end}].");

            var from = start - 1;
            if (from >= lines.Count)
                return Fail("invalid_range", $"view_range start {start} exceeds file line count {lines.Count}.");

            var to = end == -1 ? lines.Count - 1 : Math.Min(end - 1, lines.Count - 1);
            return Ok(FormatLines(lines, from, to));
        }

        // Avoid dumping very large files into the LLM context.
        if (bytes.Length > WorkspaceFileSafety.MaxViewBytes)
        {
            var previewText = text[..Math.Min(text.Length, WorkspaceFileSafety.MaxPreviewChars)];
            var previewLines = SplitLines(previewText);
            var preview = FormatLines(previewLines, 0, previewLines.Count - 1);
            return Ok(preview + $"\n... (file truncated: {bytes.Length} bytes. Use view_range to read a specific range.)");
        }

        return Ok(FormatLines(lines, 0, lines.Count - 1));
    }

    private async Task<StrReplaceEditorResult> CreateAsync(Guid taskId, string path, string? fileText, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileText))
            return Fail("io_error", "create requires a non-empty 'file_text'.");

        var fullPath = ResolvePath(taskId, path);

        if (File.Exists(fullPath))
            return Fail("io_error", $"File '{path}' already exists. Use str_replace to modify it.");

        await WorkspaceJail.WriteAllTextAsync(Root(taskId), fullPath, fileText, ct);
        _editorState.PushUndo(taskId, path, new FileSnapshot(path, null, ExistedBefore: false));
        return Ok($"File '{path}' created.");
    }

    private async Task<StrReplaceEditorResult> StrReplaceAsync(Guid taskId, string path, string? oldStr, string? newStr, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(oldStr))
            return Fail("no_match", "str_replace requires a non-empty 'old_str'.");

        var fullPath = ResolvePath(taskId, path);
        if (!File.Exists(fullPath))
            return Fail("file_not_found", $"File '{path}' not found.");

        var bytes = await WorkspaceJail.ReadAllBytesAsync(Root(taskId), fullPath, ct);
        if (IsBinary(bytes))
            return Fail("io_error", $"File '{path}' appears to be binary and cannot be edited as text.");

        var before = Encoding.UTF8.GetString(bytes);
        var occurrences = FindAllOccurrences(before, oldStr);

        if (occurrences.Count == 0)
        {
            return Fail("no_match", $"old_str not found. Check whitespace/indentation. File begins with: {Truncate(before, 200)}");
        }

        if (occurrences.Count > 1)
        {
            var lineNumbers = occurrences.Select(o => LineNumber(before, o)).ToList();
            return Fail("ambiguous_match",
                $"old_str matched {occurrences.Count} locations (lines {string.Join(", ", lineNumbers)}). Include more surrounding context to make it unique.");
        }

        var index = occurrences[0];
        var after = before.Remove(index, oldStr.Length).Insert(index, newStr ?? string.Empty);

        await WorkspaceJail.WriteAllTextAsync(Root(taskId), fullPath, after, ct);
        _editorState.PushUndo(taskId, path, new FileSnapshot(path, before, ExistedBefore: true));

        return Ok(BuildDiffContext(before, after, index));
    }

    private async Task<StrReplaceEditorResult> InsertAsync(Guid taskId, string path, string? newStr, int? insertLine, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newStr))
            return Fail("io_error", "insert requires a non-empty 'new_str'.");

        var fullPath = ResolvePath(taskId, path);
        if (!File.Exists(fullPath))
            return Fail("file_not_found", $"File '{path}' not found.");

        var text = await WorkspaceJail.ReadAllTextAsync(Root(taskId), fullPath, ct);
        if (IsBinary(Encoding.UTF8.GetBytes(text)))
            return Fail("io_error", $"File '{path}' appears to be binary and cannot be edited as text.");

        var line = insertLine ?? 0;
        var lineCount = CountLines(text);
        if (line < 0 || line > lineCount)
            return Fail("invalid_range", $"insert_line {line} is out of range (file has {lineCount} lines).");

        var inserted = InsertAfterLine(text, line, newStr);
        await WorkspaceJail.WriteAllTextAsync(Root(taskId), fullPath, inserted, ct);
        _editorState.PushUndo(taskId, path, new FileSnapshot(path, text, ExistedBefore: true));
        return Ok($"Inserted after line {line} in '{path}'.");
    }

    private StrReplaceEditorResult Undo(Guid taskId, string path)
    {
        if (!_editorState.TryPopUndo(taskId, path, out var snapshot))
            return Fail("io_error", $"Nothing to undo for '{path}'.");

        var fullPath = ResolvePath(taskId, path);

        if (snapshot.ExistedBefore)
        {
            WorkspaceJail.WriteAllTextAsync(Root(taskId), fullPath, snapshot.ContentBefore ?? string.Empty).GetAwaiter().GetResult();
        }
        else if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Ok($"Undid last edit to '{path}'.");
    }

    private Task<StrReplaceEditorResult> WithLockAsync(Guid taskId, string path, Func<Task<StrReplaceEditorResult>> action) =>
        _editorState.WithLockAsync(taskId, path, action);

    private async Task SendToolActionAsync(Guid taskId, string path, string command, string status, string? summary, CancellationToken ct)
    {
        var evt = new ToolActionEvent
        {
            ToolName = "str_replace_editor",
            Command = command,
            Path = path,
            Status = status,
            Summary = summary
        };
        await _hubContext.Clients.Group($"task_{taskId}").SendAsync("ToolAction", evt, ct);
    }

    // ---- Path safety ----

    private string ResolvePath(Guid taskId, string path) => _pathValidator.ResolveSafePath(taskId, path);

    // WORKSPACE_JAIL: добавлено 2026-09-24 — чтение/запись идут через проверенные дескрипторы (ревью C2).
    private string Root(Guid taskId) => _workspaceService.GetTaskWorkspacePath(taskId);

    // ---- Text helpers ----

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1); // drop trailing empty from a final newline
        return lines;
    }

    private static int CountLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.Length == 0) return 0;
        var count = normalized.Split('\n').Length;
        if (normalized.EndsWith('\n')) count--;
        return count;
    }

    private static string FormatLines(IReadOnlyList<string> lines, int from, int to)
    {
        if (lines.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            sb.Append(i + 1).Append('\t').Append(lines[i]);
            if (i < to) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static List<int> FindAllOccurrences(string text, string needle)
    {
        var result = new List<int>();
        var start = 0;
        while (true)
        {
            var idx = text.IndexOf(needle, start, StringComparison.Ordinal);
            if (idx < 0) break;
            result.Add(idx);
            start = idx + needle.Length;
        }
        return result;
    }

    private static int LineNumber(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static string BuildDiffContext(string before, string after, int replaceIndex)
    {
        var line = LineNumber(before, replaceIndex);
        var lines = SplitLines(after);
        if (lines.Count == 0)
            return $"Replaced 1 occurrence near line {line}.";

        var start = Math.Max(0, line - 3);
        var end = Math.Min(lines.Count - 1, line + 1);
        var sb = new StringBuilder();
        sb.AppendLine($"Replaced 1 occurrence near line {line}:");
        for (var i = start; i <= end; i++)
            sb.AppendLine($"{i + 1}\t{lines[i]}");
        return sb.ToString().TrimEnd();
    }

    private static string InsertAfterLine(string text, int lineNumber, string inserted)
    {
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var newLine = crlf ? "\r\n" : "\n";

        var lines = SplitLines(text);
        var insertLines = SplitLines(inserted);

        if (lineNumber == 0)
            lines.InsertRange(0, insertLines);
        else
            lines.InsertRange(lineNumber, insertLines);

        var joined = string.Join(newLine, lines);
        if (text.EndsWith("\n", StringComparison.Ordinal))
            joined += newLine;

        return joined;
    }

    private static string ListDirectory(string fullPath)
    {
        var entries = new List<string>();
        CollectDirectoryEntries(fullPath, 0, 2, entries);
        if (entries.Count == 0) return "Directory is empty.";
        return string.Join("\n", entries.OrderBy(e => e, StringComparer.Ordinal));
    }

    private static void CollectDirectoryEntries(string dir, int depth, int maxDepth, List<string> entries)
    {
        if (depth >= maxDepth) return;

        // WORKSPACE_JAIL: symlinked directories are not followed (review C2).
        foreach (var d in Directory.EnumerateDirectories(dir, "*", WorkspaceJail.NoLinks(recursive: false)))
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
            entries.Add(Path.GetFileName(d) + "/");
            CollectDirectoryEntries(d, depth + 1, maxDepth, entries);
        }

        foreach (var f in Directory.EnumerateFiles(dir, "*", WorkspaceJail.NoLinks(recursive: false)))
        {
            entries.Add(Path.GetFileName(f));
        }
    }

    private static bool IsBinary(byte[] bytes) => WorkspaceFileSafety.IsBinary(bytes);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n... (truncated)";

    private static StrReplaceEditorResult Fail(string errorType, string detail) =>
        new() { Success = false, ErrorType = errorType, ErrorDetail = detail };

    private static StrReplaceEditorResult Ok(string? output = null) =>
        new() { Success = true, Output = output };

    private static string BuildSummary(string command, string path)
    {
        var fileName = Path.GetFileName(path);
        return command switch
        {
            "create" => $"Создан файл {fileName}",
            "str_replace" => $"Правка {fileName}",
            "insert" => $"Вставка в {fileName}",
            "undo" => $"Откат {fileName}",
            "view" => $"Просмотр {fileName}",
            _ => $"{command} {fileName}"
        };
    }
}

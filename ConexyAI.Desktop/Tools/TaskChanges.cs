using System.Text;
using System.Text.Json;

namespace ConexyAI.Desktop.Tools;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// Итоговый diff веб-задачи (инструмент turn_diff). Как и <see cref="Checkpoints"/>, ловит записи через
/// PathGuard.BeforeWrite: при первой записи в файл за задачу запоминает исходное содержимое. Изменения
/// командами терминала сюда не попадают (как и в /rewind). Один экземпляр на корень проекта.
/// </summary>
public sealed class TaskChanges
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxTasks = 100;
    private const int MaxDiffBytes = 50 * 1024;
    private const int MaxTotalBytes = 200 * 1024;

    // Data == null && Existed — файл был, но слишком большой/нечитаемый: diff не строим.
    private sealed record Snapshot(bool Existed, byte[]? Data);

    private readonly Dictionary<Guid, Dictionary<string, Snapshot>> _tasks = new();
    private readonly List<Guid> _order = [];
    private readonly object _sync = new();

    /// <summary>Отслеживаем только задачи с сайта (у локальных ходов есть /rewind).</summary>
    public void BeforeWrite(string fullPath)
    {
        if (ToolCallContext.Current is not { Remote: true } ctx || ctx.TaskId == Guid.Empty || Directory.Exists(fullPath))
            return;
        lock (_sync)
        {
            if (!_tasks.TryGetValue(ctx.TaskId, out var files))
            {
                _tasks[ctx.TaskId] = files = new Dictionary<string, Snapshot>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                _order.Add(ctx.TaskId);
                while (_order.Count > MaxTasks)
                {
                    _tasks.Remove(_order[0]);
                    _order.RemoveAt(0);
                }
            }
            if (files.ContainsKey(fullPath))
                return;
            if (!File.Exists(fullPath))
            {
                files[fullPath] = new Snapshot(false, null);
                return;
            }
            try
            {
                files[fullPath] = new FileInfo(fullPath).Length > MaxFileBytes
                    ? new Snapshot(true, null)
                    : new Snapshot(true, File.ReadAllBytes(fullPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files[fullPath] = new Snapshot(true, null);
            }
        }
    }

    /// <summary>JSON для turn_diff; снимки задачи после этого забываются.</summary>
    public string BuildJson(Guid taskId, Func<string, string> relative)
    {
        Dictionary<string, Snapshot>? files;
        lock (_sync)
        {
            _tasks.Remove(taskId, out files);
            _order.Remove(taskId);
        }

        var result = new List<object>();
        var truncated = false;
        var total = 0;
        foreach (var (path, before) in (files ?? []).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            byte[]? after = null;
            var exists = File.Exists(path);
            var afterTooLarge = false;
            if (exists)
            {
                try
                {
                    if (new FileInfo(path).Length > MaxFileBytes) afterTooLarge = true;
                    else after = File.ReadAllBytes(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { afterTooLarge = true; }
            }

            if (!before.Existed && !exists)
                continue; // создан и удалён за задачу
            if (before.Data is not null && after is not null && before.Data.AsSpan().SequenceEqual(after))
                continue;

            var status = !before.Existed ? "added" : !exists ? "deleted" : "modified";
            var rel = relative(path);
            string diff;
            if ((before.Existed && before.Data is null) || afterTooLarge)
                diff = "Файл слишком большой для diff";
            else if (IsBinary(before.Data) || IsBinary(after))
                diff = "Бинарный файл";
            else
            {
                diff = UnifiedDiff.Create(
                    before.Existed ? Decode(before.Data!) : null,
                    exists ? Decode(after!) : null,
                    rel);
                if (diff.Length == 0)
                    continue; // отличаются только переводы строк
                if (Encoding.UTF8.GetByteCount(diff) > MaxDiffBytes)
                {
                    diff = Cut(diff, MaxDiffBytes) + "\n… (diff обрезан)\n";
                    truncated = true;
                }
            }

            var size = Encoding.UTF8.GetByteCount(diff);
            if (total + size > MaxTotalBytes)
            {
                diff = "Diff не показан: превышен общий лимит";
                truncated = true;
                size = diff.Length;
            }
            total += size;
            result.Add(new { path = rel, status, diff });
        }

        return JsonSerializer.Serialize(new { files = result, truncated }, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static bool IsBinary(byte[]? data)
    {
        if (data is null)
            return false;
        var span = data.AsSpan(0, Math.Min(data.Length, 8000));
        return span.IndexOf((byte)0) >= 0;
    }

    private static string Decode(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static string Cut(string text, int maxBytes)
    {
        // Режем по целым строкам, чтобы diff остался читаемым.
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var line in text.Split('\n'))
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > maxBytes)
                break;
            sb.Append(line).Append('\n');
            bytes += size;
        }
        return sb.ToString().TrimEnd('\n');
    }
}

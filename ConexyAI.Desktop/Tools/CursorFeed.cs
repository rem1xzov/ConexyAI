namespace ConexyAI.Desktop.Tools;

// AGENT_CURSOR: добавлено 2026-11-19 — лента «где сейчас работает каждый агент». Сервер присылает
// событие AgentCursor (координаты, цвет, инструмент); клиент складывает последние точки в файл,
// который читает оверлей с разноцветными курсорами, и отдаёт их TUI/снимкам как текстовую легенду.
/// <summary>Last known screen action of one agent on this PC.</summary>
public sealed record AgentCursorEntry(
    string AgentId,
    string AgentRole,
    string Color,
    string Tool,
    int? X,
    int? Y,
    int? ToX,
    int? ToY,
    bool? Changed,
    DateTime At);

public static class CursorFeed
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, AgentCursorEntry> Entries = new(StringComparer.Ordinal);

    /// <summary>File the overlay sidecar polls (same folder as the bubble IPC files).</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI", "cursors.txt");

    public static void Update(AgentCursorEntry entry)
    {
        lock (Sync)
        {
            Entries[entry.AgentId] = entry;
        }

        Persist();
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
        }

        Persist();
    }

    public static IReadOnlyList<AgentCursorEntry> Snapshot()
    {
        lock (Sync)
        {
            return Entries.Values
                .OrderBy(entry => entry.AgentId == "lead" ? 0 : 1)
                .ThenBy(entry => entry.AgentRole, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Text legend for the model: who else is on the screen and where (screenshots are the human's view).</summary>
    public static string Legend()
    {
        var items = Snapshot();
        if (items.Count == 0)
            return string.Empty;

        return string.Join(" · ", items.Select(entry =>
        {
            var point = entry.X is { } x && entry.Y is { } y ? $" ({x},{y})" : string.Empty;
            var changed = entry.Changed == false ? " без эффекта" : string.Empty;
            return $"{entry.AgentRole}{point}{changed}";
        }));
    }

    private static void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            // Простой построчный формат: оверлей — отдельный powershell.exe, и ему не нужен JSON-парсер.
            var lines = Snapshot()
                .Where(entry => entry.X is not null && entry.Y is not null)
                .Select(entry =>
                    $"{entry.Color}|{entry.X}|{entry.Y}|{entry.AgentRole.Replace('|', '/')}|" +
                    (entry.Changed == false ? "0" : "1"));
            // Пишем атомарно: оверлей читает файл параллельно и не должен увидеть половину строки.
            var temporary = FilePath + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Подсказка для оверлея — не повод ломать прогон.
        }
    }
}

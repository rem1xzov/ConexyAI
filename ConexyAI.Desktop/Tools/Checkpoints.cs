namespace ConexyAI.Desktop.Tools;

/// <summary>
/// Контрольные точки для /rewind (как в Claude Code): перед первой записью в файл за ход агента
/// запоминаем, каким файл был (или что его не было). Откат возвращает проект к состоянию до хода.
/// Все записи инструментов проходят через <see cref="PathGuard.ResolveForWrite"/>, поэтому ловятся
/// в одном месте. Изменения, сделанные командами терминала (npm install, git, сборка), не отслеживаются —
/// так же, как в Claude Code.
/// </summary>
public sealed class Checkpoints
{
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private const long MaxTurnBytes = 200L * 1024 * 1024;
    private const int MaxTurns = 30;

    public sealed class Turn(string prompt)
    {
        public string Prompt { get; } = prompt;
        public DateTime At { get; } = DateTime.Now;
        // null — файла до хода не было (откат его удалит).
        public Dictionary<string, byte[]?> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Untracked { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Bytes { get; set; }
    }

    private readonly List<Turn> _turns = [];
    private readonly object _sync = new();
    private Turn? _current;

    public IReadOnlyList<Turn> Turns
    {
        get { lock (_sync) return _turns.Where(t => t.Originals.Count > 0).ToList(); }
    }

    /// <summary>Начало хода агента: дальнейшие записи относятся к нему.</summary>
    public void BeginTurn(string prompt)
    {
        lock (_sync)
        {
            _current = new Turn(prompt.Length > 80 ? prompt[..80] + "…" : prompt);
            _turns.Add(_current);
            while (_turns.Count > MaxTurns)
                _turns.RemoveAt(0);
        }
    }

    /// <summary>Вызывается перед записью: сохраняет исходное состояние файла один раз за ход.</summary>
    public void BeforeWrite(string fullPath)
    {
        lock (_sync)
        {
            if (_current is null || _current.Originals.ContainsKey(fullPath) || _current.Untracked.Contains(fullPath))
                return;
            if (Directory.Exists(fullPath))
                return; // папка-назначение (копирование) — её содержимое тут не снимаем
            if (!File.Exists(fullPath))
            {
                _current.Originals[fullPath] = null;
                return;
            }
            var size = new FileInfo(fullPath).Length;
            if (size > MaxFileBytes || _current.Bytes + size > MaxTurnBytes)
            {
                _current.Untracked.Add(fullPath);
                return;
            }
            try
            {
                _current.Originals[fullPath] = File.ReadAllBytes(fullPath);
                _current.Bytes += size;
            }
            catch (IOException) { _current.Untracked.Add(fullPath); }
            catch (UnauthorizedAccessException) { _current.Untracked.Add(fullPath); }
        }
    }

    /// <summary>Откатывает последние <paramref name="count"/> ходов с изменениями (от новых к старым).</summary>
    public (int Restored, int Deleted, List<string> Failed, List<string> Untracked) Rewind(int count)
    {
        lock (_sync)
        {
            var withChanges = _turns.Where(t => t.Originals.Count > 0 || t.Untracked.Count > 0).ToList();
            var undo = withChanges.TakeLast(Math.Clamp(count, 1, withChanges.Count)).Reverse().ToList();
            int restored = 0, deleted = 0;
            var failed = new List<string>();
            var untracked = undo.SelectMany(t => t.Untracked).Distinct().ToList();
            foreach (var turn in undo)
            {
                foreach (var (path, original) in turn.Originals)
                {
                    try
                    {
                        if (original is null)
                        {
                            if (File.Exists(path))
                            {
                                File.Delete(path);
                                deleted++;
                            }
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            File.WriteAllBytes(path, original);
                            restored++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed.Add(path);
                    }
                }
                _turns.Remove(turn);
            }
            _current = null;
            return (restored, deleted, failed, untracked);
        }
    }
}

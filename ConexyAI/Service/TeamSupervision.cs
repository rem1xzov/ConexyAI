using System.Security.Cryptography;
using System.Text;

namespace ConexyAI.Service;

// ORCHESTRA_SUPERVISION: единый слой телеметрии команды.
// Хранит последние действия каждого помощника, находит повторяющиеся циклы/отсутствие прогресса
// и отдаёт ведущему компактный, но полный по последним действиям отчёт. Не привязан к конкретному
// инструменту или ошибке: любые действие+аргументы+результат сравниваются как сигнатуры.

public sealed record TeamActivity(
    DateTime At,
    string Kind,
    string Tool,
    string Summary,
    string Output,
    bool IsError,
    bool Progress);

public sealed class TeamMonitor
{
    private const int MaxEntries = 160;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(25);
    private readonly object _sync = new();
    private readonly List<TeamActivity> _entries = new();
    private DateTime _lastAlertAt = DateTime.MinValue;
    private string _lastSignature = string.Empty;
    private bool _cycle;

    public int LoopWarnings { get; private set; }
    public string? Diagnosis { get; private set; }
    public bool LoopSuspected => _cycle;

    /// <summary>Ведущий вмешался — даём помощнику чистый шанс выйти из цикла.</summary>
    public void ClearLoop()
    {
        lock (_sync)
        {
            _cycle = false;
            LoopWarnings = 0;
            Diagnosis = null;
            _lastSignature = string.Empty;
        }
    }

    /// <summary>Добавляет действие и, если замечен цикл, возвращает готовую инструкцию агенту.</summary>
    public string? Record(string kind, string tool, string summary, string output, bool isError, bool progress)
    {
        string? alert;
        lock (_sync)
        {
            var entry = new TeamActivity(
                DateTime.UtcNow,
                kind,
                tool,
                Safe(summary, 220),
                Safe(output, 600),
                isError,
                progress);
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);

            if (progress)
            {
                // Прогресс сам по себе не отменяет проверку: повторяющаяся сборка/тест с тем же
                // выводом — тоже цикл. Но если цикла не нашли, счётчик предупреждений сбрасываем.
                if (!DetectLoop().HasValue)
                {
                    _cycle = false;
                    LoopWarnings = 0;
                    Diagnosis = null;
                    _lastSignature = string.Empty;
                }
            }

            if (kind != "tool")
                return null;

            var detection = DetectLoop();
            if (detection is null)
                return null;

            Diagnosis = detection.Value.Diagnosis;
            var now = DateTime.UtcNow;
            var signature = detection.Value.Signature;
            var changed = !_cycle || !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
            _cycle = true;
            _lastSignature = signature;
            if (!changed && now - _lastAlertAt < AlertCooldown)
                return null;

            _lastAlertAt = now;
            LoopWarnings++;
            alert = $"[Harness] Похоже на цикл: {Diagnosis}. " +
                    "Смени подход: не повторяй ту же последовательность действий. " +
                    "Сделай следующий шаг другим способом либо заверши работу кратким отчётом.";
        }
        return alert;
    }

    public void RecordNote(string kind, string summary)
    {
        lock (_sync)
        {
            _entries.Add(new TeamActivity(DateTime.UtcNow, kind, string.Empty, Safe(summary, 220), string.Empty, false, false));
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }

    public IReadOnlyList<TeamActivity> Snapshot(int max = 8)
    {
        lock (_sync)
            return _entries.TakeLast(Math.Max(1, max)).ToArray();
    }

    public string BuildActivityText(int max = 8, int outputLimit = 180)
    {
        var entries = Snapshot(max);
        if (entries.Count == 0)
            return "действий пока нет";
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var time = entry.At.ToLocalTime().ToString("HH:mm:ss");
            var icon = entry.IsError ? "!" : entry.Progress ? "+" : "·";
            var output = Clip(entry.Output, outputLimit);
            sb.Append("  ").Append(time).Append(' ').Append(icon).Append(' ').Append(entry.Tool);
            if (entry.Summary.Length > 0)
                sb.Append(' ').Append(entry.Summary);
            if (output.Length > 0)
                sb.Append(" -> ").Append(output);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private (string Diagnosis, string Signature)? DetectLoop()
    {
        var tools = _entries.Where(e => e.Kind == "tool").TakeLast(48).ToList();
        if (tools.Count < 6)
            return null;

        // A B A B: повторяющийся блок любой длины 2..16 — это общий признак цикла,
        // а не конкретная команда. Разные команды в той же последовательности тоже ловятся.
        for (var length = 2; length <= 16 && tools.Count >= length * 2; length++)
        {
            var last = tools.Skip(tools.Count - length).ToArray();
            var previous = tools.Skip(tools.Count - length * 2).Take(length).ToArray();
            if (!SameSequence(last, previous))
                continue;

            // Точный повтор разрешён только если блок не состоит целиком из прогрессивных правок:
            // повторная сборка/тест с тем же результатом — тоже цикл, а две разные правки — нет.
            var signature = string.Join("|", last.Select(e => e.Tool + ":" + Hash(e.Summary + e.Output)));
            return ($"последовательность из {length} действий повторяется без нового результата", signature);
        }

        // Последние 10 действий не дали ни одной правки/успешной команды и выдача повторяется.
        var tail = tools.TakeLast(10).ToArray();
        if (tail.Length == 10 && tail.All(e => !e.Progress))
        {
            var hashes = tail.Select(e => Hash(e.Output)).Distinct().Count();
            if (hashes <= 3)
                return ("десять действий без нового результата", "no-progress:" + hashes);
        }

        // Один и тот же класс ошибки подряд с разными аргументами.
        var errors = tools.TakeLast(6).Where(e => e.IsError).ToArray();
        if (errors.Length >= 4 && errors.Select(e => Hash(e.Output.Split('\n').FirstOrDefault() ?? "")).Distinct().Count() == 1)
            return ("повторяется один и тот же класс ошибки", "same-error:" + Hash(errors[^1].Output));

        return null;
    }

    private static bool SameSequence(TeamActivity[] a, TeamActivity[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            // Для цикла важна повторяющаяся ПОСЛЕДОВАТЕЛЬНОСТЬ вызовов (инструмент+аргументы):
            // одинаковый набор команд в том же порядке — уже петля, даже если вывод содержит
            // таймстампы/ID и побайтово не совпадает. Содержательные различия ловит no-progress.
            if (!string.Equals(a[i].Tool, b[i].Tool, StringComparison.Ordinal)
                || !string.Equals(Hash(a[i].Summary), Hash(b[i].Summary), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string Hash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes, 0, 6);
    }

    private static string Safe(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string Clip(string value, int max)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}

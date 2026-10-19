using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ConexyAI.Service;

// LOOP_GUARD: добавлено 2026-09-27
/// <summary>
/// Защита от зацикливания внутри ОДНОГО запуска агента.
/// <para>
/// Держит скользящее окно последних вызовов инструментов и отказывает в повторе ДО того, как команда
/// уйдёт в терминал: если одна и та же команда уже три раза подряд вернула одно и то же, четвёртый
/// запуск ничего нового не даст, а токены и время съест. Отдельно считает обращения к интернету
/// (поиск + чтение страниц) — но только внутри ОДНОГО хода модели: счётчик сбрасывается на каждом
/// шаге цикла, поэтому за задачу поиск не ограничен, ограничен лишь «взрыв» поисков в одном ответе.
/// </para>
/// <para>
/// Экземпляр создаётся на прогон (в <c>RunLoopAsync</c>), а не живёт в сервисе: петля — свойство
/// конкретной задачи, и состояние не должно перетекать в следующий запуск.
/// </para>
/// </summary>
public sealed partial class AgentLoopGuard
{
    /// <summary>Сколько одинаковых вызовов с одинаковым результатом подряд считаются петлёй.</summary>
    public const int RepeatThreshold = 3;

    /// <summary>
    /// Сколько раз инструмент должен вернуть ОДИН И ТОТ ЖЕ результат (даже с разными аргументами),
    /// чтобы это считалось топтанием на месте: модель перебирает имена файлов и команды, а вывод не меняется.
    /// </summary>
    public const int NoProgressThreshold = 4;

    /// <summary>После стольких отказов подряд инструменты отключаются и модель обязана дать текстовый итог.</summary>
    public const int StopAfterRefusals = 3;

    /// <summary>
    /// Сколько раз можно обратиться к ОДНОМУ И ТОМУ ЖЕ объекту чтения (файл, шаблон, папка), пока не внесена
    /// правка. Агент один раз перечитывал один файл десятки раз с разными параметрами и не переходил к делу.
    /// </summary>
    public const int ReadTargetThreshold = 3;

    /// <summary>
    /// Сколько интернет-обращений (web_search + fetch_web_page) разрешено за ОДИН ход модели.
    /// Счётчик сбрасывается на каждом шаге цикла (<see cref="ResetWebBudget"/>), поэтому в целом за
    /// задачу поиск не ограничен — ограничен только «взрыв» поисков внутри одного ответа.
    /// </summary>
    public const int WebCallBudget = 10;

    /// <summary>Текст отказа при повторе. Модель читает его как результат инструмента.</summary>
    public const string RepeatRefusal =
        "Команда уже выполнялась несколько раз без изменения результата. Прекрати повторять одно и то же " +
        "действие. Проанализируй причину неудачи или заверши работу, объяснив пользователю проблему.";

    /// <summary>Текст отказа, когда результат не меняется от попытки к попытке.</summary>
    public const string NoProgressRefusal =
        "Ты уже получал ровно этот же результат несколько раз подряд, меняя только аргументы. Новых данных это не даст: " +
        "остановись, проанализируй, что уже известно, и либо сделай следующий шаг другим способом, либо дай итог пользователю.";

    /// <summary>Текст отказа, когда модель проигнорировала несколько отказов подряд.</summary>
    public const string LoopStopRefusal =
        "Инструменты отключены: ты повторял одни и те же действия без нового результата. Дай итоговый ответ текстом — " +
        "что сделано, что не получилось и что нужно от пользователя.";

    /// <summary>Текст отказа, когда один и тот же объект читается снова и снова.</summary>
    public const string ReadTargetRefusal =
        "Ты уже читал этот файл/объект несколько раз, и данные у тебя есть. Хватит читать — примени то, что уже " +
        "прочитал: внеси правку, выполни команду или дай итог. Повторное чтение с другими параметрами нового не даст.";

    /// <summary>Текст отказа, когда исчерпан бюджет чтения за прогон.</summary>
    public const string ReadBudgetRefusal =
        "Бюджет чтения на этот запуск исчерпан. Больше не читай — действуй: правка файла или команда, затем проверка.";

    /// <summary>Текст отказа, когда интернет-бюджет этого хода исчерпан.</summary>
    public const string WebBudgetRefusal =
        "Лимит интернет-запросов на этот ход исчерпан. Не повторяй поиск сейчас — обработай уже " +
        "найденную информацию и продолжай задачу.";

    /// <summary>Инструменты чтения: повторный запрос к тому же объекту ничего не добавляет.</summary>
    public static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "file_read", "file_read_bytes", "grep", "glob", "workspace_list_files",
        "view_image", "read_document_file", "project_rules",
    };

    /// <summary>Оболочки: команду внутри ещё надо распознать как чтение или действие.</summary>
    public static bool IsShellTool(string tool) => tool is "bash" or "terminal_exec" or "cmd_exec";

    /// <summary>
    /// Читающая команда оболочки (Get-Content, Get-ChildItem, cat, head, git log…). Агент обходил защиту
    /// ровно так: десятки `bash Get-Content -Skip N` вместо инструмента чтения.
    /// </summary>
    public static bool IsReadOnlyCommand(string argumentsJson)
    {
        var command = ReadCommand(argumentsJson);
        if (string.IsNullOrWhiteSpace(command))
            return false;
        // Запись, цепочки и подстановки — это уже действие, а не чтение.
        if (command.Contains('>') || command.Contains(';') || command.Contains("&&", StringComparison.Ordinal) ||
            command.Contains("||", StringComparison.Ordinal) || command.Contains("$(", StringComparison.Ordinal) ||
            command.Contains('`') || command.Contains('\n') || command.Contains('\r'))
            return false;
        return command.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(segment => SafeCommandHead().IsMatch(segment));
    }

    private static string? ReadCommand(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("command", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?:get-content|gc|get-childitem|gci|dir|ls|cat|type|head|tail|grep|rg|select-string|sls|findstr|wc|where|which|pwd|tree|stat|file|sort|uniq|diff|find|git\s+(?:status|log|diff|show|branch|remote|rev-parse|ls-files|blame))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SafeCommandHead();

    /// <summary>Инструменты, которые ходят в интернет и считаются в общий бюджет.</summary>
    private static readonly HashSet<string> WebTools = new(StringComparer.Ordinal)
    {
        "web_search",
        "fetch_web_page"
    };

    private readonly int _repeatThreshold;
    private readonly int _webCallBudget;
    private readonly int _windowSize;
    private readonly List<Call> _recent = new();
    private readonly Dictionary<string, int> _targets = new(StringComparer.Ordinal);

    public AgentLoopGuard(
        int repeatThreshold = RepeatThreshold,
        int webCallBudget = WebCallBudget,
        int windowSize = 8)
    {
        _repeatThreshold = Math.Max(1, repeatThreshold);
        _webCallBudget = Math.Max(0, webCallBudget);
        // Окно не может быть короче порога повторов, иначе петлю не из чего было бы собрать.
        _windowSize = Math.Max(_repeatThreshold, windowSize);
    }

    /// <summary>Сколько интернет-обращений уже израсходовано в этом ходе.</summary>
    public int WebCallsUsed { get; private set; }

    /// <summary>Сколько вызовов отклонено из-за петли. По нему раннер решает отключить инструменты.</summary>
    public int Refusals { get; private set; }

    /// <summary>Пора заканчивать: модель проигнорировала отказы и продолжает топтаться на месте.</summary>
    public bool ShouldStop => Refusals >= StopAfterRefusals;

    /// <summary>
    /// Открывает новый бюджет интернет-обращений — вызывается в начале каждого хода модели. За один
    /// ответ (ход) разрешено не больше <see cref="WebCallBudget"/> обращений, но на следующем шаге
    /// счётчик снова полон: за задачу поиск не ограничен, ограничен лишь взрыв поисков внутри хода.
    /// </summary>
    public void ResetWebBudget() => WebCallsUsed = 0;

    /// <summary>
    /// Можно ли выполнять вызов. <c>null</c> — можно; иначе причина отказа, которую надо вернуть
    /// модели вместо результата инструмента. Для интернет-инструментов бюджет списывается здесь же,
    /// поэтому метод вызывается РОВНО один раз на вызов инструмента.
    /// </summary>
    public string? Refuse(string tool, string arguments)
    {
        if (IsRead(tool, arguments) && _targets.GetValueOrDefault(Target(tool, arguments)) >= ReadTargetThreshold)
        {
            Refusals++;
            return ReadTargetRefusal;
        }

        if (RefusesRepeat(tool, arguments))
        {
            Refusals++;
            return RepeatRefusal;
        }

        // Разные аргументы, но один и тот же результат: перебор имён файлов и вариантов команды.
        if (RefusesNoProgress(tool))
        {
            Refusals++;
            return NoProgressRefusal;
        }

        if (WebTools.Contains(tool))
        {
            if (WebCallsUsed >= _webCallBudget)
            {
                return WebBudgetRefusal;
            }

            WebCallsUsed++;
        }

        return null;
    }

    /// <summary>
    /// Запоминает фактический результат — только по нему и видно, что повтор ничего не меняет
    /// (упавшая команда и та же команда с новым выводом — разные ситуации).
    /// </summary>
    /// <summary>Отказ по бюджету чтения: раннер сам решает, когда чтения стало слишком много.</summary>
    public string RefuseRead()
    {
        Refusals++;
        return ReadBudgetRefusal;
    }

    public void Record(string tool, string arguments, string output)
    {
        _recent.Add(new Call(tool, arguments, Hash(output)));
        // Любая правка/команда — прогресс: счётчики чтения обнуляются, перечитывать после правки нормально.
        if (IsProgressTool(tool, arguments))
            _targets.Clear();
        else if (IsRead(tool, arguments))
        {
            var key = Target(tool, arguments);
            _targets[key] = _targets.GetValueOrDefault(key) + 1;
        }

        if (_recent.Count > _windowSize)
        {
            _recent.RemoveRange(0, _recent.Count - _windowSize);
        }
    }

    /// <summary>
    /// Последние <see cref="NoProgressThreshold"/> результатов ЭТОГО инструмента совпали, даже если аргументы
    /// менялись. Вызовы других инструментов между ними не рвут цепочку: в петле модель обычно чередует
    /// «записать → пересчитать → прочитать» с одним и тем же выводом.
    /// </summary>
    public bool RefusesNoProgress(string tool)
    {
        var seen = 0;
        string? output = null;
        for (var i = _recent.Count - 1; i >= 0 && seen < NoProgressThreshold; i--)
        {
            var call = _recent[i];
            if (!string.Equals(call.Tool, tool, StringComparison.Ordinal))
                continue;
            output ??= call.OutputHash;
            if (!string.Equals(call.OutputHash, output, StringComparison.Ordinal))
                return false;
            seen++;
        }
        return seen >= NoProgressThreshold;
    }

    /// <summary>
    /// Три (и больше) одинаковых вызова ПОДРЯД, и все они дали один и тот же вывод. Любой другой
    /// вызов между ними рвёт цепочку: «упало → поправил → снова упало» — это нормальная работа,
    /// а не петля.
    /// </summary>
    public bool RefusesRepeat(string tool, string arguments)
    {
        var matching = 0;
        string? output = null;

        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var call = _recent[i];
            if (!string.Equals(call.Tool, tool, StringComparison.Ordinal) ||
                !string.Equals(call.Arguments, arguments, StringComparison.Ordinal))
            {
                break;
            }

            output ??= call.OutputHash;
            if (!string.Equals(call.OutputHash, output, StringComparison.Ordinal))
            {
                // Вывод менялся от попытки к попытке — это попытки, а не петля.
                return false;
            }

            matching++;
        }

        return matching >= _repeatThreshold;
    }

    /// <summary>Чтение — либо инструмент чтения, либо читающая команда оболочки.</summary>
    public static bool IsRead(string tool, string arguments) =>
        ReadTools.Contains(tool) || IsShellTool(tool) && IsReadOnlyCommand(arguments);

    /// <summary>Ключ объекта чтения без пагинации: тот же файл с другим view_range — тот же объект.</summary>
    private static string Target(string tool, string arguments)
    {
        if (IsShellTool(tool))
            return tool + "|" + NormalizeCommand(ReadCommand(arguments) ?? arguments);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return tool + "|" + arguments;
            var parts = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (PagingKeys.Contains(property.Name))
                    continue;
                parts.Add(property.Name + "=" + property.Value.GetRawText());
            }
            return tool + "|" + string.Join(";", parts);
        }
        catch (JsonException)
        {
            return tool + "|" + arguments;
        }
    }

    /// <summary>
    /// Одинаковые по сути читающие команды: `Get-Content f -Skip 100 -TotalCount 100` и `-Skip 200` —
    /// тот же файл. Поэтому убираем числа, лишние пробелы и кавычки.
    /// </summary>
    private static string NormalizeCommand(string command)
    {
        var sb = new StringBuilder(command.Length);
        var space = false;
        foreach (var c in command.ToLowerInvariant())
        {
            if (char.IsDigit(c) || c is '\'' or '"')
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }
            sb.Append(c);
            space = false;
        }

        // Флаги пагинации не делают команду другой: `-Skip`, `-TotalCount`, `head`/`tail` — всё то же чтение.
        var kept = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !PagingTokens.Contains(token));
        return string.Join(' ', kept);
    }

    private static readonly HashSet<string> PagingTokens = new(StringComparer.Ordinal)
    {
        "-skip", "-first", "-last", "-totalcount", "-n", "-c", "-l", "-lines", "-offset", "-limit",
        "--skip", "--first", "--last", "--limit", "--offset", "--lines", "head", "tail",
    };

    private static readonly HashSet<string> PagingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "view_range", "offset", "limit", "max_results", "lines", "tail_lines", "head_lines",
        "context_lines", "start", "end", "from", "to", "length", "start_line", "end_line", "count",
    };

    /// <summary>Правка или команда — значит, работа идёт, и счётчики чтения можно сбросить.</summary>
    public static bool IsProgressTool(string tool, string arguments) => tool switch
    {
        "file_write" or "file_write_bytes" or "file_patch" or "apply_patch" => true,
        // Запуск сборки/тестов — действие, а `Get-Content`/`Get-ChildItem` — всё ещё чтение.
        "bash" or "terminal_exec" or "cmd_exec" => !IsReadOnlyCommand(arguments),
        "run_tests" or "get_diagnostics" => true,
        "bg_start" or "bg_stop" or "install_software" => true,
        "str_replace_editor" => !arguments.Contains("\"view\"", StringComparison.Ordinal),
        _ => false,
    };

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private readonly record struct Call(string Tool, string Arguments, string OutputHash);
}

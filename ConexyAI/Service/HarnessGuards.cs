using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// USER_CONSTRAINTS: добавлено 2026-11-20 — явные запреты пользователя («не трогай tests/», «не меняй
// конфиг», «только читай», «не коммить») должны исполняться системой, а не надеждой на послушание
// модели. Это общий механизм, а не защита тестов: те же правила закрывают «не трогай кнопку X»,
// «не коммить», «только посмотри». Гейт стоит в общем конвейере отказов и НЕ отключает инструменты —
// на запрещённый вызов отвечает отказ с предложением спросить пользователя через `ask_user`.
public sealed class UserConstraints
{
    // READ_ONLY: «ничего не меняй / только читай».
    private static readonly Regex ReadOnlyPattern = new(
        @"(?i)(только\s+(читай|чтение|посмотри|посмотреть)|без\s+правок|ничего\s+не\s+(меняй|трогай|правь)|" +
        @"read[- ]?only|do\s+not\s+(change|modify)\s+(any\s+)?files|don'?t\s+(change|modify)\s+(any\s+)?files)",
        RegexOptions.Compiled);

    // FORBIDDEN: «не трогай/не меняй/не редактируй/не удаляй X».
    private static readonly Regex ForbiddenPattern = new(
        @"(?i)(?:не\s+(?:трогай|трогать|меняй|менять|редактируй|редактировать|изменяй|изменять|правь|править|" +
        @"перезаписывай|перезаписывать|удаляй|удалять|запускай|запускать)|" +
        @"(?:do\s+not|don'?t)\s+(?:touch|modify|edit|change|delete|run))\s+(?:файл[аы]?\s+|file\s+|файлы\s+|" +
        @"папк[уи]\s+|directory\s+|folder\s+)?(?<target>[^\s,.;:!?)]+)",
        RegexOptions.Compiled);

    // GIT_WRITE: «не коммить / не пушить / не делай git commit».
    private static readonly Regex GitWritePattern = new(
        @"(?i)(не\s+(?:коммить|коммит|пушить|пуш|делай\s+(?:git\s+)?commit)|(?:do\s+not|don'?t)\s+(?:commit|push))",
        RegexOptions.Compiled);

    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "file_write", "file_write_bytes", "file_patch", "apply_patch", "str_replace_editor",
    };

    // READ_ONLY_COMMANDS: что можно запускать в режиме «только читай» (консервативно, без записи).
    private static readonly Regex ReadOnlyCommand = new(
        @"^\s*(?:ls|dir|cat|type|head|tail|wc|stat|file|pwd|echo|grep|rg|findstr|find|where|which|" +
        @"git\s+(?:status|diff|log|show|branch|remote|rev-parse|ls-files)|" +
        @"(?:dotnet|python|python3|node|npm|npx|go|cargo|java|mvn|gradle)\s+(?:--version|--list-sdks|--help|version)|" +
        @"test\s+-|Test-Path|Get-Item|Get-ChildItem|Get-Content|Select-String|Measure-Object|Resolve-Path)\b",
        RegexOptions.Compiled);

    // GIT_WRITE_COMMANDS: git-команды, которые меняют историю/репозиторий.
    private static readonly Regex GitWriteCommand = new(
        @"(?i)\bgit\s+(?:commit|push|reset|rebase|merge|checkout|switch|stash|clean|tag|remote\s+add|" +
        @"cherry-pick|revert|fetch|pull)\b",
        RegexOptions.Compiled);

    private static readonly Regex TestTarget = new(@"(?i)(тест|tests?|spec)", RegexOptions.Compiled);
    // Конфиг/README — частые объекты запрета, которые не выглядят как путь, но имеют понятный шаблон.
    private static readonly Regex ConfigTarget = new(@"(?i)(конфиг|config)", RegexOptions.Compiled);
    private static readonly Regex DocsTarget = new(@"(?i)(readme|докум|docs?)", RegexOptions.Compiled);
    // PATH_LIKE: в путь превращаем только явные пути/файлы с расширением. Иначе «не меняй API»
    // блокировало бы любой файл с «api» в имени — запрет должен быть точным.
    private static readonly Regex PathLike = new(@"[/\\]|\.[a-z0-9]{1,6}$", RegexOptions.Compiled);

    private UserConstraints(bool readOnly, IReadOnlyList<string> forbidden, bool noGitWrite)
    {
        ReadOnly = readOnly;
        Forbidden = forbidden;
        NoGitWrite = noGitWrite;
    }

    public bool ReadOnly { get; }

    /// <summary>Нормализованные запрещённые цели: пути/подстроки имён (например «tests/», «test_»).</summary>
    public IReadOnlyList<string> Forbidden { get; }

    public bool NoGitWrite { get; }

    public bool Any => ReadOnly || Forbidden.Count > 0 || NoGitWrite;

    /// <summary>Разбирает запреты из сообщения пользователя (первое сообщение хода).</summary>
    public static UserConstraints FromPrompt(string? prompt)
    {
        var text = prompt ?? string.Empty;
        var forbidden = new List<string>();
        foreach (Match match in ForbiddenPattern.Matches(text))
        {
            var target = CleanTarget(match.Groups["target"].Value);
            if (target.Length == 0)
                continue;
            if (TestTarget.IsMatch(target))
            {
                foreach (var pattern in new[] { "tests/", "test_", "_test.", "spec" })
                    Add(forbidden, pattern);
                continue;
            }
            if (ConfigTarget.IsMatch(target))
            {
                Add(forbidden, "config");
                continue;
            }
            if (DocsTarget.IsMatch(target))
            {
                Add(forbidden, "readme");
                continue;
            }
            // Свободное слово («поведение», «API») — не путь: не превращаем его в фильтр по имени файла.
            if (!PathLike.IsMatch(target))
                continue;
            Add(forbidden, target);
        }

        return new UserConstraints(ReadOnlyPattern.IsMatch(text), forbidden, GitWritePattern.IsMatch(text));
    }

    /// <summary>Причина отказа или <c>null</c> — действие не противоречит запретам пользователя.</summary>
    public string? Refuse(string tool, string arguments)
    {
        if (!Any)
            return null;

        if (tool is "bash" or "terminal_exec" or "cmd_exec")
        {
            var command = ReadString(arguments, "command") ?? string.Empty;
            if (ReadOnly && !IsReadOnlyCommand(command))
                return "Пользователь запретил менять файлы (режим «только чтение»). Эта команда не выглядит читающей — " +
                       "не выполняй её; если без неё не обойтись, спроси через `ask_user`.";
            if (NoGitWrite && GitWriteCommand.IsMatch(command))
                return "Пользователь запретил менять git (commit/push/reset и т. п.). Не выполняй эту команду; " +
                       "если действие действительно нужно — спроси через `ask_user`.";
            return null;
        }

        var paths = WritePaths(tool, arguments);
        if (ReadOnly && WriteTools.Contains(tool))
            return "Пользователь запретил менять файлы (режим «только чтение»). Правка отклонена; " +
                   "если изменение действительно нужно — спроси через `ask_user`.";

        if (Forbidden.Count == 0 || paths.Count == 0)
            return null;
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
            if (Forbidden.Any(item => normalized.Contains(item, StringComparison.OrdinalIgnoreCase)))
                return $"Пользователь явно запретил трогать «{path}». Правка отклонена; " +
                       "если без неё задача действительно не решается — спроси через `ask_user`.";
        }

        return null;
    }

    private static void Add(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            values.Add(value);
    }

    /// <summary>Очищает цель запрета: кавычки, markdown, завершающая пунктуация.</summary>
    private static string CleanTarget(string target)
    {
        var value = target.Trim().Trim('`', '"', '\'', '«', '»', '*', '_');
        value = value.TrimEnd('.', ',', ';', ':', ')' , '(').Trim();
        return value.Length > 120 ? value[..120] : value;
    }

    private static string? ReadString(string arguments, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Пути, которые изменит вызов: path/файл и заголовки unified diff.</summary>
    private static List<string> WritePaths(string tool, string arguments)
    {
        var paths = new List<string>();
        if (tool is not ("file_write" or "file_write_bytes" or "file_patch" or "apply_patch" or "str_replace_editor"))
            return paths;
        if (ReadString(arguments, "path") is { Length: > 0 } path)
            paths.Add(path);
        if (tool == "apply_patch" && ReadString(arguments, "patch") is { Length: > 0 } patch)
        {
            foreach (Match match in Regex.Matches(patch, @"^\+\+\+\s+(?:[ab]/)?(?<p>\S+)", RegexOptions.Multiline))
            {
                var value = match.Groups["p"].Value.Trim();
                if (value.Length > 0 && value != "/dev/null")
                    paths.Add(value);
            }
        }
        return paths;
    }

    private static bool IsReadOnlyCommand(string command)
    {
        var value = command.Trim();
        return value.Length > 0 && ReadOnlyCommand.IsMatch(value)
               && !value.Contains('>', StringComparison.Ordinal)
               && !value.Contains("Remove-Item", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("New-Item", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("Set-Content", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Гейт конвейера: запреты пользователя проверяются перед выполнением инструмента.</summary>
public sealed class UserConstraintGate(UserConstraints constraints) : IToolGate
{
    public string Name => "user_constraints";

    public string? Refuse(string tool, string arguments) => constraints.Refuse(tool, arguments);
}

// TOOL_ADVICE: добавлено 2026-11-20 — в выводе ошибки часто уже написан готовый совет («consider
// renaming numbers.py…», «did you mean…», «run pip install…»). В стресс-тесте агент перебирал флаги
// import-mode вместо этого совета. Детектор вытаскивает такую строку и нуджит модели применить её.
public static class ToolAdviceDetector
{
    private static readonly Regex[] Patterns =
    [
        new(@"(?i)(consider\s+renaming[^\n]{0,200})", RegexOptions.Compiled),
        new(@"(?i)([^\n]{0,160}same\s+name\s+as\s+the\s+standard\s+library[^\n]{0,160})", RegexOptions.Compiled),
        new(@"(?i)((?:did|do)\s+you\s+mean[^\n]{0,160})", RegexOptions.Compiled),
        new(@"(?i)((?:maybe|perhaps)\s+you\s+meant[^\n]{0,160})", RegexOptions.Compiled),
        new(@"(?i)((?:try|run)\s+(?:running\s+)?[`'""][^`'""\n]{2,80}[`'""][^\n]{0,120}to\s+(?:install|fix|resolve)[^\n]{0,120})", RegexOptions.Compiled),
        new(@"(?i)((?:hint|note|tip)\s*:[^\n]{0,200})", RegexOptions.Compiled),
        new(@"(?i)(to\s+fix\s+this[^\n]{0,200})", RegexOptions.Compiled),
    ];

    private const int MaxAdviceChars = 260;

    /// <summary>Готовый совет из вывода ошибки или <c>null</c>, если его там нет.</summary>
    public static string? Detect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(output);
            if (!match.Success)
                continue;
            var advice = match.Groups[1].Value.Trim();
            if (advice.Length == 0)
                continue;
            if (advice.Length > MaxAdviceChars)
                advice = advice[..MaxAdviceChars] + "…";
            return $"[Advice] В выводе инструмента уже есть готовый совет: «{advice}». " +
                   "Примени именно его, а не перебирай флаги/варианты команды; если совет неверен — объясни почему и продолжи.";
        }

        return null;
    }
}

// BATCH_DEDUP: добавлено 2026-11-20 — модель может запросить в ОДНОМ ответе два одинаковых вызова
// (стресс-тест: два `run_tests` с теми же аргументами). Подсказка Repeat срабатывает после батча и
// такой дубль не ловит. Дедуп действует только на идемпотентные инструменты (чтение/проверка),
// инструменты с побочными эффектами выполняются как есть.
public sealed class BatchCallDeduplicator
{
    private static readonly HashSet<string> Idempotent = new(StringComparer.Ordinal)
    {
        "file_read", "file_read_bytes", "workspace_list_files", "grep", "glob", "repo_map",
        "run_tests", "get_diagnostics", "agents_status", "view_image", "read_document_file",
        "read_document_chunk", "search_user_chats", "search_documents", "read_skill",
    };

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Новый батч — одинаковые вызовы между разными батчами не дубли.</summary>
    public void Reset() => _seen.Clear();

    /// <summary><c>true</c> — такой вызов в этом батче уже был; выполнять не нужно.</summary>
    public bool ShouldSkip(string tool, string? arguments)
    {
        if (!Idempotent.Contains(tool))
            return false;
        return !_seen.Add(tool + "\u0000" + RepeatToolReminder.Canonicalize(arguments));
    }
}

// HARNESS_CLAIMS: добавлено 2026-11-20 — запрет на финальный ответ «мне отключили инструменты»,
// когда инструменты в этом прогоне были доступны. Формулировки узкие: честные «нет доступа к API»,
// «пользователь отклонил» и внешние блокеры не считаются отпиской об инструментах.
public static class HarnessClaims
{
    private static readonly Regex ToolsDisabledPattern = new(
        @"(?i)(инструменты\s+(?:были\s+)?(?:отключены|отключили|недоступны|заблокированы)|" +
        @"мне\s+отключили\s+инструменты|tool(?:s)?\s+(?:are|were|got)\s+(?:disabled|unavailable)|" +
        @"tool\s+access\s+(?:is|was)\s+disabled|calls?\s+to\s+tools?\s+are\s+disabled)",
        RegexOptions.Compiled);

    /// <summary>Финальный текст заявляет, что инструменты отключены системой.</summary>
    public static bool ClaimsToolsDisabled(string? finalText) =>
        !string.IsNullOrWhiteSpace(finalText) && ToolsDisabledPattern.IsMatch(finalText);
}

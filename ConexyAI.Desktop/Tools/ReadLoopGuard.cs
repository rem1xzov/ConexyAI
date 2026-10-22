using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

// LOOP_GUARD: добавлено 2026-10-19 — агент читал один и тот же файл десятки раз (в том числе через
// `bash Get-Content -Skip N`) и не переходил к правкам. Сервер тоже это ловит, но заслон в клиенте
// работает и со старой версией сервера: задача не может бесконечно жечь токены на чтение.
/// <summary>Счётчик чтений без единой правки/команды. Живёт один сеанс ToolRunner.</summary>
public sealed partial class ReadLoopGuard
{
    /// <summary>Сколько раз можно прочитать ОДИН И ТОТ ЖЕ объект, пока не внесена правка.</summary>
    public const int TargetLimit = 2;

    /// <summary>Сколько чтений подряд без действий допустимо за сеанс.</summary>
    public const int StreakLimit = 10;

    private readonly Dictionary<string, int> _targets = new(StringComparer.Ordinal);
    private int _streak;

    // EDIT_AFTER_READ: файлы, прочитанные в этом сеансе. Правка файла без чтения запрещена —
    // ровно как в Claude Code: модель не может «переписать» код, который не видела, иначе
    // старая версия файла теряется целиком.
    private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);

    // BASH_FILE_OPS: какую команду уже отговаривали — отказ ровно один раз, чтобы не запереть
    // агента, если альтернативы действительно нет, но и не дать ему молча обходить инструменты.
    private readonly HashSet<string> _bashWarned = new(StringComparer.Ordinal);

    /// <summary>Новый ход: счётчики чтения начинаются заново.</summary>
    public void Reset()
    {
        _streak = 0;
        _targets.Clear();
        _read.Clear();
        _bashWarned.Clear();
    }

    /// <summary>Чтение ли это. Для команд — по тому же признаку, что и подтверждение (только чтение).</summary>
    public static bool IsRead(string tool, ToolArgs args)
    {
        switch (tool)
        {
            case "file_read":
            case "file_read_bytes":
            case "grep":
            case "glob":
            case "workspace_list_files":
                return true;
            case "str_replace_editor":
                return args.Str("command") == "view";
            case "bash":
            case "terminal_exec":
            case "cmd_exec":
                return IsReadOnlyScript(args.Str("command") ?? string.Empty);
            default:
                return false;
        }
    }

    /// <summary>Почему вызов надо отклонить (null — можно выполнять).</summary>
    /// <param name="fileExists">Проверка «файл уже есть»: правка существующего файла требует чтения,
    /// создание нового — нет (иначе агент не смог бы создать ни одного файла).</param>
    public string? Refuse(string tool, ToolArgs args, Func<string, bool>? fileExists = null)
    {
        if (BashFileOpRefusal(tool, args) is { } bashRefusal)
            return bashRefusal;
        if (WriteRefusal(tool, args, fileExists) is { } writeRefusal)
            return writeRefusal;

        if (!IsRead(tool, args))
            return null;

        var target = Target(tool, args);
        if (_targets.GetValueOrDefault(target) >= TargetLimit)
            return "Этот файл/объект уже прочитан несколько раз — данные у тебя есть. Не читай снова: " +
                   "внеси правку, выполни команду или дай итог. Повторное чтение с другими параметрами нового не даст.";
        if (_streak >= StreakLimit)
            return $"Уже {_streak} чтений подряд без единой правки. Хватит читать: примени прочитанное — " +
                   "внеси правку файла или выполни команду, затем проверь результат.";
        return null;
    }

    /// <summary>Учитывает выполненный вызов: чтение — в счётчик и в список прочитанных, правка сбрасывает всё.</summary>
    public void Record(string tool, ToolArgs args)
    {
        if (IsRead(tool, args))
        {
            RememberRead(tool, args);
            var target = Target(tool, args);
            // Новый объект — прогресс: серия «читаю и не правлю» считается только по повторным чтениям.
            if (!_targets.ContainsKey(target))
                _streak = 0;
            else
                _streak++;
            _targets[target] = _targets.GetValueOrDefault(target) + 1;
            return;
        }

        if (IsAction(tool, args))
        {
            _streak = 0;
            _targets.Clear();
        }
    }

    /// <summary>
    /// Читающий ли скрипт. `CommandPolicy.IsReadOnly` намеренно строг (нужен для подтверждений) и отказывает
    /// любой команде с `;`. Агент этим пользовался: `$t=[IO.File]::ReadAllText(...); $t.Substring(...)` —
    /// формально цепочка, фактически чтение, и счётчик чтений сбрасывался.
    /// </summary>
    private static bool IsReadOnlyScript(string command)
    {
        var lower = command.ToLowerInvariant();
        if (lower.Length == 0)
            return false;
        if (ShellWriteTokens.Any(token => lower.Contains(token, StringComparison.Ordinal)))
            return false;
        if (lower.Contains(">>", StringComparison.Ordinal) || lower.Contains(" > ", StringComparison.Ordinal))
            return false;
        return ShellReadTokens.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }

    private static readonly string[] ShellReadTokens =
    {
        "get-content", "get-childitem", "gci ", "select-string", "findstr", "readalltext", "readalllines",
        "readallbytes", "get-item", "test-path", "resolve-path", "select-object", "measure-object",
        "out-string", "format-table", "format-list", "where-object", "sort-object", "convertto-json",
        "rg ", "grep", "cat ", "head ", "tail ", "wc ", "stat ", "ls ", "dir ",
        // READ_VIA_SHELL: агент читал код через `sed -n '1,60p'` и `cat` — счётчик этого не видел.
        "sed ", "awk ", "nl ",
    };

    private static readonly string[] ShellWriteTokens =
    {
        "set-content", "add-content", "out-file", "remove-item", "new-item", "copy-item", "move-item",
        "rename-item", "set-item", "clear-content", "writealltext", "writeallbytes", "appendalltext",
        "::delete", "::move", "::copy", "start-process", "invoke-expression", "iex ", "dotnet ",
        "npm ", "yarn ", "pnpm ", "gradle ", "mvn ", "msbuild ", "git add", "git commit", "git push",
        "git checkout", "git reset", "git clean", "git stash", "del ", "erase ", "rmdir",
        "sed -i", "sed --in-place",
    };

    private static bool IsAction(string tool, ToolArgs args) => tool switch
    {
        "file_write" or "file_write_bytes" or "file_patch" or "apply_patch" => true,
        "bash" or "terminal_exec" or "cmd_exec" or "run_tests" or "get_diagnostics" => true,
        "bg_start" or "bg_stop" or "install_software" or "window_keys" => true,
        "str_replace_editor" => args.Str("command") != "view",
        _ => false,
    };

    // ── EDIT_AFTER_READ ──────────────────────────────────────────────────────

    /// <summary>Запоминает файл, который агент действительно прочитал (file_read, view, grep по пути).</summary>
    private void RememberRead(string tool, ToolArgs args)
    {
        if (tool == "str_replace_editor" && args.Str("command") != "view")
            return;
        if (args.Str("path") is not { Length: > 0 } path)
            return;
        foreach (var key in Keys(path))
            _read.Add(key);
    }

    /// <summary>Правка файла, который агент не читал — отказ с точным следующим шагом.</summary>
    private string? WriteRefusal(string tool, ToolArgs args, Func<string, bool>? fileExists)
    {
        foreach (var path in WriteTargets(tool, args))
        {
            if (IsReadPath(path))
                continue;
            // Новый файл читать нечего — создание разрешено без file_read.
            if (fileExists is not null && !fileExists(path))
                continue;
            return $"Сначала прочитай '{path}' инструментом file_read (или str_replace_editor view), потом правь: " +
                   "иначе ты перезапишешь код, которого не видел. Это правило клиента, обойти его нельзя.";
        }
        return null;
    }

    /// <summary>Файлы, которые изменит вызов (для apply_patch — все пути из заголовков '+++').</summary>
    private static IEnumerable<string> WriteTargets(string tool, ToolArgs args)
    {
        if (tool == "apply_patch")
        {
            var patch = args.Str("patch") ?? string.Empty;
            foreach (Match match in PatchPath().Matches(patch))
            {
                var path = match.Groups["p"].Value.Trim();
                if (path.Length > 0 && path != "/dev/null")
                    yield return path;
            }
            if (args.Str("path") is { Length: > 0 } fallback)
                yield return fallback;
            yield break;
        }

        if (tool == "str_replace_editor" && args.Str("command") is "view")
            yield break;

        if ((tool is "file_write" or "file_patch" or "str_replace_editor" or "file_write_bytes")
            && args.Str("path") is { Length: > 0 } target)
            yield return target;
    }

    private bool IsReadPath(string path) => Keys(path).Any(_read.Contains);

    /// <summary>
    /// Ключи пути: нормализованный путь и его последние два сегмента. Один и тот же файл модель
    /// называет то относительно корня, то абсолютно — сравнение по хвосту склеивает эти формы.
    /// </summary>
    private static IEnumerable<string> Keys(string path)
    {
        var norm = path.Replace('\\', '/').Trim().TrimEnd('/');
        while (norm.StartsWith("./", StringComparison.Ordinal))
            norm = norm[2..];
        if (norm.Length > 1 && norm[1] == ':')
            norm = norm[2..].TrimStart('/');
        norm = norm.ToLowerInvariant();
        yield return norm;
        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
            yield return string.Join('/', parts[^2..]);
    }

    [GeneratedRegex(@"^\+\+\+\s+(?:[ab]/)?(?<p>\S+)", RegexOptions.Multiline)]
    private static partial Regex PatchPath();

    // ── BASH_FILE_OPS ────────────────────────────────────────────────────────

    /// <summary>
    /// Файловые операции через оболочку: у них есть точный инструмент с проверкой пути и без
    /// превращения текста в скрипт. Отказ один раз на команду: если агент настаивает — выполняем.
    /// </summary>
    private string? BashFileOpRefusal(string tool, ToolArgs args)
    {
        if (tool is not ("bash" or "terminal_exec" or "cmd_exec"))
            return null;
        var command = args.Str("command") ?? string.Empty;
        if (command.Length == 0)
            return null;
        var suggestion = FileOpSuggestion(command);
        if (suggestion is null)
            return null;
        if (!_bashWarned.Add(NormalizeToken(command)))
            return null;
        return $"Файлы не читают и не пишут через bash. Вместо этой команды: {suggestion}. " +
               "Так правка идёт через проверку пути и показывается в ленте отдельным шагом. " +
               "Если другого способа правда нет — повтори ту же команду, и она выполнится.";
    }

    private static string? FileOpSuggestion(string command)
    {
        var lower = command.ToLowerInvariant();
        if (lower.Contains("set-content", StringComparison.Ordinal) || lower.Contains("out-file", StringComparison.Ordinal)
            || lower.Contains("writealltext", StringComparison.Ordinal) || lower.Contains("writealllines", StringComparison.Ordinal))
            return "file_write (новый файл) или apply_patch (правка существующего)";
        if (lower.Contains("add-content", StringComparison.Ordinal) || lower.Contains("appendalltext", StringComparison.Ordinal))
            return "apply_patch, добавив строки в нужное место файла";
        if (lower.Contains("get-content", StringComparison.Ordinal) || lower.Contains("readalltext", StringComparison.Ordinal))
            return "file_read (весь файл) или grep (нужные строки)";
        if (lower.Contains("select-string", StringComparison.Ordinal) && lower.Contains("-path", StringComparison.Ordinal))
            return "grep";
        if (iSedInPlace().IsMatch(lower))
            return "apply_patch со старыми и новыми строками";
        if (iCatRedirect().IsMatch(lower))
            return "file_write";
        // READ_VIA_SHELL: чтение кода через `sed -n`/`cat`/`head`/`grep` — работа file_read и grep.
        if (iShellTextReader().IsMatch(lower))
            return "file_read (весь файл, с пагинацией) или grep (нужные строки)";
        if (iFindByName().IsMatch(lower))
            return "glob";
        return null;
    }

    private static string NormalizeToken(string command) =>
        Digits().Replace(command.Trim().ToLowerInvariant(), "#");

    [System.Text.RegularExpressions.GeneratedRegex(@"\bsed\s+(-[a-z]*i|--in-place)")]
    private static partial System.Text.RegularExpressions.Regex iSedInPlace();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(cat|type)\b[^\n]*>")]
    private static partial System.Text.RegularExpressions.Regex iCatRedirect();

    [System.Text.RegularExpressions.GeneratedRegex(@"(^|[\s;&|(])(cat|head|tail|nl|sed|awk|grep|rg)\s")]
    private static partial System.Text.RegularExpressions.Regex iShellTextReader();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bfind\b[^\n]*\s(-name|-iname|-type)\b")]
    private static partial System.Text.RegularExpressions.Regex iFindByName();

    /// <summary>Ключ объекта: файл/шаблон/папка. Для команд числа (номера строк) не важны.</summary>
    private static string Target(string tool, ToolArgs args)
    {
        if (tool is "bash" or "terminal_exec" or "cmd_exec")
            return tool + "|" + ShellTarget(args.Str("command") ?? string.Empty);

        var parts = new List<string> { tool };
        foreach (var key in new[] { "path", "pattern", "glob", "window", "url" })
        {
            if (args.Str(key) is { } value)
                parts.Add(key + "=" + value);
        }
        // PAGING: у file_read страницы — штатный способ читать большой файл: offset/limit входят в ключ,
        // иначе третий offset считался бы повтором того же чтения.
        if (tool is "file_read" or "file_read_bytes")
        {
            if (args.Int("offset") is { } offset)
                parts.Add("offset=" + offset);
            if (args.Int("limit") is { } limit)
                parts.Add("limit=" + limit);
        }
        return string.Join('|', parts);
    }

    /// <summary>
    /// Объект чтения для команды: файл, о котором она, а не текст скрипта. Модель читала один файл
    /// десятками разных однострочников (ReadAllText/ReadAllLines/Select-String) — текст разный, файл тот же.
    /// </summary>
    private static string ShellTarget(string command)
    {
        var match = FilePathInCommand().Match(command);
        return match.Success
            ? match.Value.Replace('\\', '/').ToLowerInvariant()
            : Digits().Replace(command, string.Empty).ToLowerInvariant();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[A-Za-z]:)?[A-Za-z0-9_.\-/\\]+\.(?:cs|js|ts|tsx|jsx|css|html|htm|json|md|py|csproj|sln|txt|xml|yml|yaml|props|targets|ps1|sh|bat)")]
    private static partial System.Text.RegularExpressions.Regex FilePathInCommand();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\s)-?\d+|\d+")]
    private static partial System.Text.RegularExpressions.Regex Digits();
}

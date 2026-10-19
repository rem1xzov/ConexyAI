using System.Text.Json;

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

    /// <summary>Новый ход: счётчики чтения начинаются заново.</summary>
    public void Reset()
    {
        _streak = 0;
        _targets.Clear();
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
    public string? Refuse(string tool, ToolArgs args)
    {
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

    /// <summary>Учитывает выполненный вызов: чтение — в счётчик, правка/команда — сбрасывает всё.</summary>
    public void Record(string tool, ToolArgs args)
    {
        if (IsRead(tool, args))
        {
            _streak++;
            var target = Target(tool, args);
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
    };

    private static readonly string[] ShellWriteTokens =
    {
        "set-content", "add-content", "out-file", "remove-item", "new-item", "copy-item", "move-item",
        "rename-item", "set-item", "clear-content", "writealltext", "writeallbytes", "appendalltext",
        "::delete", "::move", "::copy", "start-process", "invoke-expression", "iex ", "dotnet ",
        "npm ", "yarn ", "pnpm ", "gradle ", "mvn ", "msbuild ", "git add", "git commit", "git push",
        "git checkout", "git reset", "git clean", "git stash", "del ", "erase ", "rmdir",
    };

    private static bool IsAction(string tool, ToolArgs args) => tool switch
    {
        "file_write" or "file_write_bytes" or "file_patch" or "apply_patch" => true,
        "bash" or "terminal_exec" or "cmd_exec" or "run_tests" or "get_diagnostics" => true,
        "bg_start" or "bg_stop" or "install_software" or "window_keys" => true,
        "str_replace_editor" => args.Str("command") != "view",
        _ => false,
    };

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

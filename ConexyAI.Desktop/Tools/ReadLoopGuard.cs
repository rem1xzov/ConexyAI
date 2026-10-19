using System.Text.Json;

namespace ConexyAI.Desktop.Tools;

// LOOP_GUARD: добавлено 2026-10-19 — агент читал один и тот же файл десятки раз (в том числе через
// `bash Get-Content -Skip N`) и не переходил к правкам. Сервер тоже это ловит, но заслон в клиенте
// работает и со старой версией сервера: задача не может бесконечно жечь токены на чтение.
/// <summary>Счётчик чтений без единой правки/команды. Живёт один сеанс ToolRunner.</summary>
public sealed partial class ReadLoopGuard
{
    /// <summary>Сколько раз можно прочитать ОДИН И ТОТ ЖЕ объект, пока не внесена правка.</summary>
    public const int TargetLimit = 3;

    /// <summary>Сколько чтений подряд без действий допустимо за сеанс.</summary>
    public const int StreakLimit = 15;

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
                return CommandPolicy.IsReadOnly(args.Str("command") ?? string.Empty);
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
            return tool + "|" + Digits().Replace(args.Str("command") ?? string.Empty, string.Empty).ToLowerInvariant();

        var parts = new List<string> { tool };
        foreach (var key in new[] { "path", "pattern", "glob", "window", "url" })
        {
            if (args.Str(key) is { } value)
                parts.Add(key + "=" + value);
        }
        return string.Join('|', parts);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\s)-?\d+|\d+")]
    private static partial System.Text.RegularExpressions.Regex Digits();
}

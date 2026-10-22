using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// EDIT_AFTER_READ + BASH_FILE_OPS: добавлено 2026-10-22 — контракт из Claude Code, которого не хватало.
//
// 1. Правка файла, который агент не читал в этом прогоне, отклоняется: иначе `file_write` затирает
//    код, которого модель не видела, и вместе со старой версией теряются детали, которых нет
//    в её представлении о файле.
// 2. Файловые операции через оболочку (`Get-Content`, `Set-Content`, `sed -i`, `Out-File`, `cat >`)
//    отклоняются один раз с указанием точного инструмента: у инструментов есть проверка пути,
//    запись в ленту отдельным шагом и никакого превращения текста в скрипт.
//
// Отказ ровно один раз на команду: если агент повторит её, она выполнится — защита от запирания,
// когда альтернативы действительно нет. Правка без чтения не выполняется никогда.
public sealed partial class EditAfterReadGuard
{
    private readonly Func<string, bool>? _fileExists;
    private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bashWarned = new(StringComparer.Ordinal);

    /// <param name="fileExists">Проверяет, существует ли файл: правка существующего требует чтения,
    /// создание нового — нет. Без функции считается, что файл существует (безопаснее).</param>
    public EditAfterReadGuard(Func<string, bool>? fileExists = null) => _fileExists = fileExists;

    /// <summary>Причина отказа или null, если вызов можно выполнять.</summary>
    public string? Refuse(string tool, string argumentsJson)
    {
        if (BashFileOpRefusal(tool, argumentsJson) is { } bashRefusal)
            return bashRefusal;
        return WriteRefusal(tool, argumentsJson);
    }

    /// <summary>Учитывает успешно выполненный вызов: запоминает прочитанные файлы.</summary>
    public void Record(string tool, string argumentsJson)
    {
        if (!IsReadTool(tool, argumentsJson))
            return;
        if (ReadPath(argumentsJson) is { Length: > 0 } path)
            AddReadPath(path);
    }

    private string? WriteRefusal(string tool, string argumentsJson)
    {
        foreach (var path in WriteTargets(tool, argumentsJson))
        {
            if (IsReadPath(path))
                continue;
            if (_fileExists is not null && !_fileExists(path))
                continue;
            return $"Сначала прочитай '{path}' инструментом file_read (или str_replace_editor view), потом правь: " +
                   "иначе ты перезапишешь код, которого не видел. Это правило сервера, обойти его нельзя.";
        }
        return null;
    }

    private string? BashFileOpRefusal(string tool, string argumentsJson)
    {
        if (!AgentLoopGuard.IsShellTool(tool))
            return null;
        var command = StringArg(argumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command))
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
        if (SedInPlace().IsMatch(lower))
            return "apply_patch со старыми и новыми строками";
        if (CatRedirect().IsMatch(lower))
            return "file_write";
        // READ_VIA_SHELL: прогон на реальном проекте — 50+ чтений кода через `sed -n`/`cat`/`head` и ни одного
        // file_read. У инструментов есть пагинация и запись в ленту, у оболочки — только текст в контекст.
        if (ShellTextReader().IsMatch(lower))
            return "file_read (весь файл, с пагинацией) или grep (нужные строки)";
        if (FindByName().IsMatch(lower))
            return "glob";
        return null;
    }

    private static bool IsReadTool(string tool, string argumentsJson) => tool switch
    {
        "file_read" or "file_read_bytes" or "grep" or "glob" or "view_image" or "read_document_file" => true,
        "str_replace_editor" => StringArg(argumentsJson, "command") == "view",
        _ => false,
    };

    private static string? ReadPath(string argumentsJson) => StringArg(argumentsJson, "path");

    private static IEnumerable<string> WriteTargets(string tool, string argumentsJson)
    {
        if (tool == "apply_patch")
        {
            var patch = StringArg(argumentsJson, "patch") ?? string.Empty;
            foreach (Match match in PatchPath().Matches(patch))
            {
                var path = match.Groups["p"].Value.Trim();
                if (path.Length > 0 && path != "/dev/null")
                    yield return path;
            }
            if (StringArg(argumentsJson, "path") is { Length: > 0 } fallback)
                yield return fallback;
            yield break;
        }

        if (tool is not ("file_write" or "file_write_bytes" or "file_patch" or "create_document" or "str_replace_editor"))
            yield break;
        if (tool == "str_replace_editor" && StringArg(argumentsJson, "command") is "view")
            yield break;
        if (StringArg(argumentsJson, "path") is { Length: > 0 } target)
            yield return target;
    }

    private void AddReadPath(string path)
    {
        foreach (var key in Keys(path))
            _read.Add(key);
    }

    private bool IsReadPath(string path) => Keys(path).Any(_read.Contains);

    /// <summary>
    /// Ключи пути: нормализованная форма и последние два сегмента. Один и тот же файл модель называет
    /// то относительно корня, то абсолютно (в том числе с путём рабочей области) — хвост склеивает эти формы.
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

    private static string NormalizeToken(string command) =>
        Digits().Replace(command.Trim().ToLowerInvariant(), "#");

    private static string? StringArg(string argumentsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\+\+\+\s+(?:[ab]/)?(?<p>\S+)", RegexOptions.Multiline)]
    private static partial Regex PatchPath();

    [GeneratedRegex(@"\bsed\s+(-[a-z]*i|--in-place)")]
    private static partial Regex SedInPlace();

    [GeneratedRegex(@"\b(cat|type)\b[^\n]*>")]
    private static partial Regex CatRedirect();

    // Чтение текста файлов оболочкой: cat/head/tail/sed/awk/nl, а также grep/rg по файлам — это работа file_read и grep.
    [GeneratedRegex(@"(^|[\s;&|(])(cat|head|tail|nl|sed|awk|grep|rg)\s")]
    private static partial Regex ShellTextReader();

    [GeneratedRegex(@"\bfind\b[^\n]*\s(-name|-iname|-type)\b")]
    private static partial Regex FindByName();

    [GeneratedRegex(@"(?<=\s)-?\d+|\d+")]
    private static partial Regex Digits();
}

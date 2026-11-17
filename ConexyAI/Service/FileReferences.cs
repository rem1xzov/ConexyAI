using System.Text.RegularExpressions;

namespace ConexyAI.Service;

/// <summary>
/// FILE_REFERENCES: «@путь» в просьбе пользователя — это ссылка на файл, а не команда вставить его содержимое.
/// Перенесено из deepseek-harness (<c>context/file-reference/src/index.ts:72</c>): «Tokens prefixed with @
/// are workspace paths the user explicitly referenced… use the read tool when its contents are needed, and do
/// not claim to have inspected it before reading». Выигрыш: десятки килобайт файла не уезжают в контекст
/// на каждом шаге, а модель читает только то, что ей действительно нужно.
/// </summary>
public static partial class FileReferences
{
    /// <summary>Пути, упомянутые через @. Содержимое файлов не читается.</summary>
    public static IReadOnlyList<string> Extract(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (Match match in Mention().Matches(prompt))
        {
            var path = match.Groups[1].Value.Trim().Replace('\\', '/');
            path = path.TrimStart('.', '/');
            if (path.Length == 0 || !seen.Add(path))
                continue;
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Блок для промпта: только пути и требование прочитать их инструментом.</summary>
    public static string Prompt(IReadOnlyList<string> paths) =>
        "[Файлы, на которые ссылается пользователь]\n" +
        string.Join("\n", paths.Select(p => "- " + p)) + "\n" +
        "Это только пути: содержимое не вставлено намеренно. Прочитай нужные файлы инструментом read, " +
        "прежде чем утверждать, что видел их содержимое.";

    [GeneratedRegex(@"(?<![\w@\/])@([\w.\/-]+\.[A-Za-z0-9]{1,8})")]
    private static partial Regex Mention();
}

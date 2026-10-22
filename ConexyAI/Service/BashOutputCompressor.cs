using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// BASH_COMPRESS: добавлено 2026-10-22 — вывод сборки/тестов едет в контекст на каждом шаге.
// Сырой лог MSBuild на 40k символов — это ~10k токенов, из которых полезны итог и окно вокруг ошибки.
// Компрессор делает три вещи: снимает ANSI и баннеры, выбрасывает простыни пустых строк и для
// сборки/тестов оставляет «итог + окно вокруг ошибок». Обычный вывод режется по краям, как раньше.
public static partial class BashOutputCompressor
{
    /// <summary>Потолок для обычного вывода (как head+tail в прежней версии, но в символах).</summary>
    public const int MaxChars = 14_000;

    /// <summary>Потолок для сырого режима (run_tests): нужен весь вывод, но не гигабайт.</summary>
    public const int MaxRawChars = 400_000;

    public static (string Text, bool Truncated) Compress(string? raw, string command, bool keepAlmostAll = false)
    {
        if (string.IsNullOrEmpty(raw))
            return (string.Empty, false);

        var text = Clean(raw);

        if (keepAlmostAll)
            return text.Length <= MaxRawChars ? (text, false) : HeadTail(text, MaxRawChars);

        if (LooksLikeBuildOrTest(command) && ErrorLine().IsMatch(text))
        {
            var compact = CompactBuild(text);
            if (compact.Length > 0)
                return (compact, true);
        }

        return HeadTail(text, MaxChars);
    }

    /// <summary>ANSI-последовательности, CRLF, баннеры тулчейнов и простыни пустых строк.</summary>
    private static string Clean(string raw)
    {
        var text = Ansi().Replace(raw, string.Empty);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Banner().Replace(text, string.Empty);
        text = BlankRun().Replace(text, "\n\n");
        return text.TrimEnd();
    }

    private static (string Text, bool Truncated) HeadTail(string text, int max)
    {
        if (text.Length <= max)
            return (text, false);
        var head = (int)(max * 0.6);
        var tail = max - head;
        return (text[..head]
                + $"\n[... вывод обрезан: {text.Length - max} символов ...]\n"
                + text[^tail..], true);
    }

    /// <summary>«Итог + окно вокруг ошибки»: сводка сборки/тестов и по 2 строки контекста на ошибку.</summary>
    private static string CompactBuild(string text)
    {
        var lines = text.Split('\n');
        var summary = lines.Where(l => SummaryLine().IsMatch(l)).TakeLast(8).ToList();

        var errorIndexes = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (ErrorLine().IsMatch(lines[i]))
                errorIndexes.Add(i);
        }

        var picked = new SortedSet<int>();
        foreach (var index in errorIndexes.Take(12))
        {
            for (var i = Math.Max(0, index - 2); i <= Math.Min(lines.Length - 1, index + 2); i++)
                picked.Add(i);
        }

        var body = new StringBuilder();
        foreach (var index in picked)
            body.Append(lines[index]).Append('\n');
        if (errorIndexes.Count > 12)
            body.Append($"... ещё ошибок: {errorIndexes.Count - 12}\n");

        var compact = new StringBuilder();
        compact.Append($"[вывод сжат: {lines.Length} строк → итог и окно вокруг ошибок]\n");
        if (summary.Count > 0)
            compact.Append(string.Join('\n', summary)).Append("\n\n");
        var bodyText = body.ToString().TrimEnd();
        if (bodyText.Length > 6_000)
            bodyText = bodyText[..6_000] + "\n[... ошибки обрезаны ...]";
        compact.Append(bodyText);
        return compact.ToString().TrimEnd();
    }

    private static bool LooksLikeBuildOrTest(string command)
    {
        var c = (command ?? string.Empty).ToLowerInvariant();
        return BuildCommand().IsMatch(c);
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)")]
    private static partial Regex Ansi();

    [GeneratedRegex(
        @"^\s*(Determining projects to restore|Welcome to \.NET|MSBuild version|Copyright \(C\) Microsoft|\[?v?stest\]? ?$)\s*\n?",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Banner();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun();

    [GeneratedRegex(
        @"\b(dotnet\s+(build|test|publish|restore)|msbuild|gradlew?|mvn|npm\s+(run\s+)?(build|test)|yarn\s+(build|test)|pnpm\s+(build|test)|cargo\s+(build|test|check)|go\s+(build|test|vet)|pytest|mocha|jest|vitest|tsc|make)\b")]
    private static partial Regex BuildCommand();

    [GeneratedRegex(
        @"\b\d+\s+(Error|Warning)\(s\)|Build (succeeded|FAILED)|Passed!|Failed!|Tests run:|Total tests:|Test Run (Failed|Successful)|Time Elapsed|error (CS|MSB|NU|NETSDK)\d+|: error\b|\bfailed\b|\bFAIL(ED)?\b|Traceback|Unhandled exception|fatal error|undefined reference|Cannot find module|Module not found|✗")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(
        @"^\s*(\d+\s+(Error|Warning)\(s\)|Build (succeeded|FAILED).*|Passed!.*|Failed!.*|Tests run:.*|Total tests:.*|Test Run (Failed|Successful).*|Time Elapsed:.*|\d+ (passed|failed|passing|failing).*)\s*$")]
    private static partial Regex SummaryLine();
}

using System.Text;

namespace ConexyAI.Desktop.Tools;

// STATIC_WEB: добавлено 2026-11-21 — статический сайт/PWA без package.json раньше давал
// «не удалось определить тип проекта» и на run_tests, и на get_diagnostics (2 провала в стресс-тесте).
// Теперь для таких проектов клиент сам проверяет: синтаксис каждого *.js через `node --check`,
// JSON манифеста/конфигов через парсер, наличие service worker и его registration. Тестов нет —
// это и есть проверка «сборки»; поведение (кнопки) проверяет web_verify.
public static class StaticWebProject
{
    private static readonly string[] HtmlExtensions = [".html", ".htm"];

    public static bool IsStaticWeb(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Any(file => HtmlExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static async Task<ToolResult> RunChecksAsync(string root, ShellTool shell, bool tests, CancellationToken ct)
    {
        var output = new StringBuilder();
        var problems = new List<string>();
        output.AppendLine(tests
            ? "Статический сайт: тест-раннера нет — проверяю синтаксис JS, JSON и PWA-файлы (для поведения используй web_verify)."
            : "Статический сайт: проверяю синтаксис JS, JSON и PWA-файлы.");

        List<string> jsFiles;
        try
        {
            jsFiles = Directory.EnumerateFiles(root, "*.js", SearchOption.TopDirectoryOnly).OrderBy(file => file).Take(20).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail("Не удалось прочитать папку проекта: " + ex.Message);
        }

        if (jsFiles.Count == 0 && !File.Exists(Path.Combine(root, "index.html")))
            return ToolResult.Fail("Статический проект без JS и index.html — проверять нечего.");

        foreach (var file in jsFiles)
        {
            var name = Path.GetFileName(file);
            var result = await shell.RunTrustedAsync($"node --check \"{name}\"", 60, ct);
            if (result.IsError)
            {
                problems.Add(name + ": синтаксис JS не прошёл");
                output.AppendLine($"  ✗ {name}: {FirstLine(result.Output)}");
            }
            else
            {
                output.AppendLine($"  ✓ {name}: синтаксис ок");
            }
        }

        foreach (var json in new[] { "manifest.webmanifest", "manifest.json" })
        {
            var path = Path.Combine(root, json);
            if (!File.Exists(path))
                continue;
            try
            {
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).Dispose();
                output.AppendLine($"  ✓ {json}: валидный JSON");
            }
            catch (System.Text.Json.JsonException ex)
            {
                problems.Add(json + ": невалидный JSON");
                output.AppendLine($"  ✗ {json}: {ex.Message}");
            }
        }

        var sw = Path.Combine(root, "sw.js");
        if (File.Exists(sw))
        {
            var source = File.ReadAllText(sw);
            var handlers = (source.Contains("addEventListener('install'") || source.Contains("addEventListener(\"install\""))
                           && (source.Contains("addEventListener('fetch'") || source.Contains("addEventListener(\"fetch\""));
            output.AppendLine(handlers ? "  ✓ sw.js: install/fetch найдены" : "  ! sw.js: нет install/fetch-обработчиков");
            if (!handlers)
                problems.Add("sw.js: нет install/fetch обработчиков");
        }

        var hasRegistration = false;
        foreach (var file in jsFiles)
        {
            try
            {
                if (File.ReadAllText(file).Contains("serviceWorker", StringComparison.OrdinalIgnoreCase))
                {
                    hasRegistration = true;
                    break;
                }
            }
            catch (IOException)
            {
            }
        }
        if (File.Exists(sw))
            output.AppendLine(hasRegistration ? "  ✓ service worker регистрируется в JS" : "  ! service worker не регистрируется в JS");

        return problems.Count == 0
            ? ToolResult.Ok(output.ToString().TrimEnd())
            : ToolResult.Fail(output.AppendLine("Проблемы: " + string.Join("; ", problems)).ToString().TrimEnd());
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "ошибка";
}

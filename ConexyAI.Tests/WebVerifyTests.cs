using System.Runtime.CompilerServices;
using System.Text.Json;
using ConexyAI.Desktop.Tools;

// WEB_VERIFY: добавлено 2026-11-21 — проверка работоспособности сайта без computer use.
// Юнит-часть (разбор шагов и JS-выражения) + интеграционный прогон на реальном headless Edge/Chrome
// по file:// странице: клик по кнопке должен менять DOM.
internal static class WebVerifyTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("web verify: steps are parsed and validated", ParseAsync);
        TestRegistry.Add("web verify: JS expressions carry selector, value and text", ExpressionsAsync);
        TestRegistry.Add("web verify: a real headless browser clicks a button and sees the DOM change",
            IntegrationAsync, () => PcTools.FindBrowser() is null ? "Edge/Chrome is not installed"
                : Environment.GetEnvironmentVariable("CONEXY_TEST_BROWSER") != "1" ? "browser test runs in real builds (conexy selftest); set CONEXY_TEST_BROWSER=1 to force" : null);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task ParseAsync()
    {
        var empty = WebVerifyTool.ParseSteps(null);
        Assert(empty.Steps.Count == 0 && empty.Error is null, "no steps is a valid empty plan");

        using var document = JsonDocument.Parse("""
            [
              {"action":"fill","selector":"#name","value":"Роза"},
              {"action":"click","selector":"#add"},
              {"action":"expect_count","selector":".card","count":1}
            ]
            """);
        var parsed = WebVerifyTool.ParseSteps(document.RootElement);
        Assert(parsed.Error is null && parsed.Steps.Count == 3, "valid steps parse: " + parsed.Error);
        Assert(parsed.Steps[0].Action == "fill" && parsed.Steps[0].Value == "Роза", "fill keeps selector and value");
        Assert(parsed.Steps[2].Count == 1, "expect_count keeps count");

        using var bad = JsonDocument.Parse("""[{"action":"click"}]""");
        Assert(WebVerifyTool.ParseSteps(bad.RootElement).Error is not null, "click without a selector is rejected");
        using var unknown = JsonDocument.Parse("""[{"action":"teleport","selector":"#x"}]""");
        Assert(WebVerifyTool.ParseSteps(unknown.RootElement).Error is not null, "an unknown action is rejected");
        return Task.CompletedTask;
    }

    private static Task ExpressionsAsync()
    {
        var click = WebVerifyTool.BuildExpression(new WebVerifyTool.Step("click", "#add", null, null, null, null));
        Assert(click.Contains("querySelector(\"#add\")", StringComparison.Ordinal) && click.Contains(".click()", StringComparison.Ordinal),
            "click expression queries the selector and calls click()");

        var fill = WebVerifyTool.BuildExpression(new WebVerifyTool.Step("fill", "#name", "Роза", null, null, null));
        Assert(fill.Contains("dispatchEvent(new Event('input'") && fill.Contains("setter.call") && fill.Contains("dispatchEvent(new Event('change'"),
            "fill expression sets the value and dispatches input/change");

        var text = WebVerifyTool.BuildExpression(new WebVerifyTool.Step("expect_text", "body", null, "готово", null, null));
        Assert(text.Contains("innerText", StringComparison.Ordinal) && text.Contains("готово", StringComparison.Ordinal),
            "expect_text expression reads innerText and compares the text");
        return Task.CompletedTask;
    }

    private static async Task IntegrationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "conexy_webverify_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var page = Path.Combine(root, "page.html");
        await File.WriteAllTextAsync(page, """
            <!doctype html>
            <html><body>
              <h1 id="title">Verdant</h1>
              <button id="add">Добавить</button>
              <div id="list"></div>
              <script>
                document.getElementById('add').addEventListener('click', function () {
                  var card = document.createElement('div');
                  card.className = 'plant-card';
                  card.textContent = 'Роза';
                  document.getElementById('list').appendChild(card);
                });
              </script>
            </body></html>
            """);

        var failed = await RunAsync(page, """
            [
              {"action":"click","selector":"#add"},
              {"action":"expect_count","selector":".plant-card","count":2},
              {"action":"expect_text","text":"Роза"}
            ]
            """);
        Assert(failed.IsError, "a wrong expected count must fail the check: " + failed.Output);

        var passed = await RunAsync(page, """
            [
              {"action":"click","selector":"#add"},
              {"action":"expect_count","selector":".plant-card","count":1},
              {"action":"expect_text","text":"Роза"},
              {"action":"expect_visible","selector":".plant-card"}
            ]
            """);
        Assert(!passed.IsError, "clicking the button must produce a visible card: " + passed.Output);
        Assert(passed.Output.Contains("ошибки JS: нет", StringComparison.Ordinal), "a healthy page reports no JS errors");

        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static async Task<ToolResult> RunAsync(string page, string stepsJson)
    {
        var args = JsonSerializer.Serialize(new
        {
            url = new Uri(page).AbsoluteUri,
            steps = JsonDocument.Parse(stepsJson).RootElement.Clone(),
            timeout_seconds = 60,
        });
        return await new WebVerifyTool().RunAsync(ToolArgs.Parse(args), CancellationToken.None);
    }
}

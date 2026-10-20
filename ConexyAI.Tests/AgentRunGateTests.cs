using System.Runtime.CompilerServices;
using ConexyAI.Service;

// RUN_GATE: добавлено 2026-10-20
/// <summary>
/// «Доведи до запуска»: сборка и проверка типов не доказывают, что приложение работает. Живой случай —
/// Expo-приложение с зелёным `tsc` и собранным бандлом, которое агент ни разу не запустил и отдал как
/// готовое. Здесь проверяется, что запуск распознаётся по факту, а не по имени инструмента, и что
/// упавшая попытка запуска за проверку не считается.
/// </summary>
internal static class AgentRunGateTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("run gate: launch commands are told apart from builds and installs", LaunchCommandsAsync);
        TestRegistry.Add("run gate: a failed start is not proof, a printed dev-server URL is", RuntimeEvidenceAsync);
        TestRegistry.Add("run gate: only projects with an entry point demand a launch", RunnableAppAsync);
        TestRegistry.Add("run gate: claims about a running app are caught", RuntimeClaimsAsync);
        TestRegistry.Add("run gate: the work-style prompt says a build is not a launch", PromptAsync);
    }

    // RUN_GATE: `npm run dev` — запуск, `npm run build`/`npm install` — нет; то же для dotnet/python/expo.
    private static Task LaunchCommandsAsync()
    {
        foreach (var command in new[]
                 {
                     "npm run dev", "npm start", "pnpm dev", "yarn start",
                     "cd frontend && npm run dev", "npm ci && npm start",
                     "npx expo start", "npx expo start --web", "expo start",
                     "npx vite", "vite", "next dev", "ng serve",
                     "dotnet run", "dotnet watch run",
                     "python main.py", "python3 app.py", "python -m http.server 3000",
                     "uvicorn main:app --reload", "flask run", "manage.py runserver",
                     "go run .", "cargo run", "docker compose up -d", "docker-compose up",
                     "node server.js",
                     "powershell -Command \"npm start\"",
                 })
        {
            Assert(ConexyAgentRunner.IsLaunchCommand(command), $"'{command}' must count as a launch");
        }

        foreach (var command in new[]
                 {
                     "npm run build", "npm install", "npm ci", "npm test", "npm run lint",
                     "npx tsc --noEmit", "npx expo export --platform web", "expo export",
                     "dotnet build", "dotnet test", "dotnet publish",
                     "pytest -q", "python -m pytest", "python build.py --check",
                     "git status", "Get-ChildItem", "eslint src",
                 })
        {
            Assert(!ConexyAgentRunner.IsLaunchCommand(command), $"'{command}' must not count as a launch");
        }

        return Task.CompletedTask;
    }

    // RUN_GATE: упавший `bg_start` ничего не доказывает; вывод с адресом dev-сервера — доказывает.
    private static Task RuntimeEvidenceAsync()
    {
        Assert(ConexyAgentRunner.IsRuntimeCheck("bg_start", """{"command":"npm run dev"}""", false, "started"),
            "a successful bg_start is runtime evidence");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("bg_start", """{"command":"npm run dev"}""", true, "EADDRINUSE"),
            "a failed bg_start proves nothing");

        Assert(ConexyAgentRunner.IsRuntimeCheck("bash", """{"command":"npm run dev"}""", false, "> ready"),
            "a successful launch through bash is evidence");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("bash", """{"command":"npm run dev"}""", true, "EADDRINUSE: port in use"),
            "a launch that failed to bind is not evidence");
        Assert(ConexyAgentRunner.IsRuntimeCheck("bash", """{"command":"npm run dev"}""", true,
                "  VITE ready in 412 ms\n  ➜  Local:   http://localhost:5173/"),
            "a command that timed out after printing the dev-server URL still proves the app started");
        Assert(ConexyAgentRunner.IsRuntimeCheck("bash", """{"command":"curl http://localhost:3000/api/health"}""", false, "{\"status\":\"ok\"}"),
            "a localhost probe is evidence");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("bash", """{"command":"npm run build"}""", false, "built in 3.2s"),
            "a build is not a launch");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("bash", """{"command":"npx tsc --noEmit"}""", false, "ok"),
            "a type check is not a launch");

        Assert(ConexyAgentRunner.IsRuntimeCheck("http_request", """{"url":"http://localhost:8081"}""", false, "200"),
            "a successful request to localhost is evidence");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("http_request", """{"url":"https://example.com"}""", false, "200"),
            "a request to a public site is not a check of the user's app");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("http_request", """{"url":"http://localhost:8081"}""", true, "refused"),
            "a failed localhost request proves nothing");

        Assert(ConexyAgentRunner.IsRuntimeCheck("page_screenshot", """{"url":"http://localhost:3000"}""", false, "png"),
            "a page screenshot is evidence");
        Assert(!ConexyAgentRunner.IsRuntimeCheck("screen_capture", """{"window":"App"}""", true, "no window"),
            "a failed screenshot is not evidence");

        return Task.CompletedTask;
    }

    // RUN_GATE: требовать запуск надо только там, где есть что запускать.
    private static Task RunnableAppAsync()
    {
        Assert(ConexyAgentRunner.LooksLikeRunnableApp(new[] { "App.tsx", "app.json", "src/screens/ChatScreen.tsx" }),
            "an Expo app has an entry point");
        Assert(ConexyAgentRunner.LooksLikeRunnableApp(new[] { "frontend/index.html", "frontend/src/main.tsx" }),
            "a web page has an entry point");
        Assert(ConexyAgentRunner.LooksLikeRunnableApp(new[] { "PonyAssistant/MainForm.cs" }),
            "a desktop window is runnable");
        Assert(ConexyAgentRunner.LooksLikeRunnableApp(new[] { "src/ConexyAI.Desktop/ConexyAI.Desktop.csproj" }),
            "a project file means a build target that also runs");
        Assert(ConexyAgentRunner.LooksLikeRunnableApp(new[] { "C:\\proj\\App.tsx" }),
            "windows paths are handled");

        Assert(!ConexyAgentRunner.LooksLikeRunnableApp(new[] { "README.md" }), "docs are not runnable");
        Assert(!ConexyAgentRunner.LooksLikeRunnableApp(new[] { "src/utils/format.ts", "src/utils/date.ts" }),
            "library files are not an entry point");
        Assert(!ConexyAgentRunner.LooksLikeRunnableApp(Array.Empty<string>()), "no changes — nothing to run");
        return Task.CompletedTask;
    }

    // RUN_GATE: «приложение запускается» — утверждение о запуске, «сборка прошла» — нет.
    private static Task RuntimeClaimsAsync()
    {
        Assert(ConexyAgentRunner.ClaimsRuntimeResult("Готово: приложение запускается без ошибок"),
            "a claim about a running app must be caught");
        Assert(ConexyAgentRunner.ClaimsRuntimeResult("Сервер поднимается и отвечает на запросы"),
            "a claim about a running service must be caught");
        Assert(!ConexyAgentRunner.ClaimsRuntimeResult("Сборка прошла, типы в порядке, правки внесены"),
            "a build report is not a runtime claim");
        Assert(!ConexyAgentRunner.ClaimsRuntimeResult(null), "empty text has no claims");
        return Task.CompletedTask;
    }

    /// <summary>Промпт обязан прямо говорить, что сборка — не доказательство работы приложения.</summary>
    private static Task PromptAsync()
    {
        var text = ConexyAI.Service.Prompts.PromptFragments.AgentWorkStyle;
        Assert(text.Contains("НЕ доказывают", StringComparison.Ordinal),
            "the work style must say a build does not prove the app runs");
        Assert(text.Contains("bg_start", StringComparison.Ordinal) && text.Contains("page_screenshot", StringComparison.Ordinal),
            "the work style must name the launch and the screenshot tools");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);
}

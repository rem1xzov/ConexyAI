using System.Runtime.CompilerServices;
using System.Text.Json;
using ConexyAI.Service;
using ConexyAI.Service.Prompts;

// LOOP_GUARD: добавлено 2026-09-27
/// <summary>
/// Страховка от зацикливания агента: повтор одной команды с тем же результатом и бюджет
/// интернет-запросов. Логика чистая (без БД и сети), поэтому проверяется напрямую.
/// </summary>
internal static class AgentLoopGuardTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("loop guard: the fourth identical call with the same output is refused", RefusesThirdRepeatAsync);
        TestRegistry.Add("loop guard: changing output or another call in between is not a loop", NotALoopAsync);
        TestRegistry.Add("loop guard: web tools are capped per model turn, and the cap resets each turn", WebBudgetAsync);
        TestRegistry.Add("loop guard: the last step asks the model to wrap up instead of stopping silently", WrapUpFragmentAsync);
        TestRegistry.Add("loop guard: different arguments with the same output stop the run", NoProgressAsync);
        TestRegistry.Add("loop guard: re-reading the same file with new ranges is refused until an edit", ReadTargetAsync);
        TestRegistry.Add("loop guard: plan and question tools never count as a loop", BookkeepingAsync);
        TestRegistry.Add("loop guard: after a read loop only file edits are allowed", WriteOnlyAsync);
        TestRegistry.Add("loop guard: reading the same file through bash with new offsets is a loop", ShellReadAsync);
        TestRegistry.Add("verify gate: a success claim without any check is caught", UnverifiedClaimsAsync);
        TestRegistry.Add("route guard: the same failure with new arguments asks for a new route", SameFailureRouteAsync);
    }

    // ROUTE_GUARD: агент перебирал имена репозитория (rem1xzv/ConoxyAI, biocompass…) и получал один и тот же
    // «Repository not found». Хеш вывода не срабатывал — в тексте ошибки был очередной адрес.
    private static Task SameFailureRouteAsync()
    {
        var guard = new AgentLoopGuard();
        var first = "remote: Repository not found.\nfatal: repository 'https://github.com/rem1xzv/biocompassa.git/' not found";
        var second = "remote: Repository not found.\nfatal: repository 'https://github.com/rem1xzv/ConoxyAI.git/' not found";

        guard.Record("bash", """{"command":"git ls-remote https://github.com/rem1xzv/biocompassa.git"}""", first, isError: true);
        Assert(guard.Refuse("bash", """{"command":"git ls-remote https://github.com/rem1xzv/ConoxyAI.git"}""") is null,
            "a single failure is not a loop yet");

        guard.Record("bash", """{"command":"git ls-remote https://github.com/rem1xzv/ConoxyAI.git"}""", second, isError: true);
        Assert(guard.Refuse("bash", """{"command":"git ls-remote https://github.com/rem1xzv/ConoxyAI2.git"}""") == AgentLoopGuard.RouteRefusal,
            "the same failure twice with different arguments must ask for a different route");
        Assert(guard.BlockedTools.Contains("bash"), "the failing tool is taken off the route");
        Assert(guard.Refuse("bash", """{"command":"git ls-remote https://github.com/rem1xzv/blah.git"}""") == AgentLoopGuard.RouteRefusal,
            "a blocked tool stays blocked until it makes progress");

        // Успешный вызов возвращает инструмент в маршрут: блокировка — не навсегда.
        guard.Record("bash", """{"command":"echo ok"}""", "ok", isError: false);
        var afterSuccess = guard.Refuse("bash", """{"command":"echo ok"}""");
        Assert(!guard.BlockedTools.Contains("bash") && afterSuccess is null,
            "a successful call unblocks the tool; blocked=" + string.Join(",", guard.BlockedTools) +
            " refusal=" + (afterSuccess ?? "null"));

        // Разные ошибки подряд — это не повтор: маршрут менять не нужно.
        var mixed = new AgentLoopGuard();
        mixed.Record("bash", """{"command":"a"}""", "error: permission denied", isError: true);
        mixed.Record("bash", """{"command":"b"}""", "fatal: unable to access: timeout", isError: true);
        Assert(mixed.Refuse("bash", """{"command":"c"}""") is null, "different failures are not a loop");
        return Task.CompletedTask;
    }

    // WRITE_ONLY: сообщение «инструменты отключены» заставляло агента сдаться, не попробовав правку.
    // Теперь после петли чтения чтения и команды запрещены, а правка — работает.
    private static Task WriteOnlyAsync()
    {
        var guard = new AgentLoopGuard();
        guard.EnterWriteOnly();
        Assert(guard.WriteOnly, "the guard must be in write-only mode");
        Assert(guard.Refuse("file_read", """{"path":"a.cs"}""") == AgentLoopGuard.WriteOnlyRefusal,
            "reading is refused in write-only mode");
        Assert(guard.Refuse("bash", """{"command":"npm test"}""") == AgentLoopGuard.WriteOnlyRefusal,
            "commands are refused in write-only mode");
        Assert(guard.Refuse("file_write", """{"path":"a.cs","content":"x"}""") is null,
            "file edits are allowed in write-only mode");
        Assert(guard.Refuse("str_replace_editor", """{"command":"view","path":"a.cs"}""") == AgentLoopGuard.WriteOnlyRefusal,
            "viewing is still reading");
        Assert(guard.Refuse("str_replace_editor", """{"command":"str_replace","path":"a.cs"}""") is null,
            "str_replace is an edit");
        Assert(guard.Refuse("create_document", """{"path":"report.docx","content":"# Итог"}""") is null,
            "creating an office document is a write");
        Assert(guard.Refuse("read_document_file", """{"path":"report.docx"}""") == AgentLoopGuard.WriteOnlyRefusal,
            "reading a document is still reading");

        guard.Record("file_write", """{"path":"a.cs","content":"x"}""", "ok");
        Assert(!guard.WriteOnly && !guard.ShouldStop, "a successful edit exits write-only mode");
        Assert(guard.Refuse("file_read", """{"path":"a.cs"}""") is null, "reads work again after the edit");

        // Если модель игнорирует и это — после трёх отказов ход всё равно завершается.
        var stubborn = new AgentLoopGuard();
        stubborn.EnterWriteOnly();
        for (var i = 0; i < AgentLoopGuard.StopAfterRefusals; i++)
            stubborn.Refuse("bash", """{"command":"ls"}""");
        Assert(stubborn.ShouldStop && stubborn.WriteOnly, "ignored write-only refusals still end the run");
        return Task.CompletedTask;
    }

    // VERIFY_CLAIMS: «всё работает» после правок без сборки/запуска — ложный отчёт, который видел пользователь.
    private static Task UnverifiedClaimsAsync()
    {
        Assert(ConexyAgentRunner.HasUnverifiedSuccessClaim("Готово: всё работает, залипание устранено", true, false, false),
            "a success claim after edits without checks must be caught");
        Assert(!ConexyAgentRunner.HasUnverifiedSuccessClaim("Всё работает", true, true, false),
            "a green build is evidence");
        Assert(!ConexyAgentRunner.HasUnverifiedSuccessClaim("Всё работает", true, false, true),
            "running the app and taking a screenshot is evidence");
        Assert(!ConexyAgentRunner.HasUnverifiedSuccessClaim("Всё работает", false, false, false),
            "without file changes another gate applies");
        Assert(!ConexyAgentRunner.HasUnverifiedSuccessClaim("Прочитал MainForm.cs и app.js, причина в pointerup", true, false, false),
            "talking about the code is not a success claim");
        Assert(!ConexyAgentRunner.HasUnverifiedSuccessClaim("Правки внесены, но сборка не проверялась", true, false, false),
            "an honest report of missing verification must pass");
        return Task.CompletedTask;
    }

    // LOOP_GUARD: агент читал файлы не инструментом file_read, а `bash Get-Content -Skip N` — такой обход
    // раньше считался действием и сбрасывал счётчик чтения.
    private static Task ShellReadAsync()
    {
        Assert(AgentLoopGuard.IsReadOnlyCommand("""{"command":"Get-Content MainForm.cs -Skip 100 -TotalCount 100"}"""),
            "PowerShell Get-Content is a read");
        Assert(AgentLoopGuard.IsReadOnlyCommand("""{"command":"Get-ChildItem -Recurse"}"""), "Get-ChildItem is a read");
        Assert(AgentLoopGuard.IsReadOnlyCommand("""{"command":"cat app.js | grep drag"}"""), "a pipe of reads is a read");
        Assert(AgentLoopGuard.IsReadOnlyCommand("""{"command":"git log -5"}"""), "git log is a read");
        Assert(!AgentLoopGuard.IsReadOnlyCommand("""{"command":"dotnet build"}"""), "a build is an action");
        Assert(!AgentLoopGuard.IsReadOnlyCommand("""{"command":"Get-Content a.cs > b.cs"}"""), "redirection writes");
        Assert(!AgentLoopGuard.IsReadOnlyCommand("""{"command":"Get-ChildItem; Remove-Item x"}"""), "chained commands are not a read");
        Assert(!AgentLoopGuard.IsReadOnlyCommand("""{"command":"git checkout main"}"""), "checkout is an action");
        // PowerShell-однострочник с `;` и `$` — чтение, если ничего не пишет.
        Assert(AgentLoopGuard.IsReadOnlyCommand(
                """{"command":"$t=[System.IO.File]::ReadAllText('a.cs',[Text.Encoding]::UTF8); $t.Substring(100,200)"}"""),
            "a PowerShell read one-liner is a read");
        Assert(!AgentLoopGuard.IsReadOnlyCommand(
                """{"command":"$p='a.cs'; Set-Content $p 'x'"}"""),
            "a script that writes is an action");

        var guard = new AgentLoopGuard();
        for (var i = 1; i <= AgentLoopGuard.ReadTargetThreshold; i++)
        {
            var args = $"{{\"command\":\"Get-Content MainForm.cs -Skip {i * 100} -TotalCount 100\"}}";
            Assert(guard.Refuse("bash", args) is null, $"shell read #{i} must be allowed");
            guard.Record("bash", args, $"chunk {i}");
        }
        Assert(guard.Refuse("bash", """{"command":"Get-Content MainForm.cs -Skip 999"}""") == AgentLoopGuard.ReadTargetRefusal,
            "the same file with another offset must be refused");

        // Разная форма скрипта — тот же файл: ReadAllText/ReadAllLines/Select-String не должны обходить предел.
        var mixed = new AgentLoopGuard();
        var scripts = new[]
        {
            "$t=[IO.File]::ReadAllText('MainForm.cs'); $t.Substring(0,100)",
            "$l=[IO.File]::ReadAllLines('MainForm.cs'); $l[10]",
            "Select-String -Path MainForm.cs -Pattern 'Drag'",
        };
        for (var i = 0; i < scripts.Length; i++)
        {
            var args = JsonSerializer.Serialize(new { command = scripts[i] });
            Assert(mixed.Refuse("bash", args) is null, $"shape #{i + 1} must be allowed");
            mixed.Record("bash", args, $"out {i}");
        }
        Assert(mixed.Refuse("bash", JsonSerializer.Serialize(new { command = "$t=[IO.File]::ReadAllText('MainForm.cs'); $t" }))
               == AgentLoopGuard.ReadTargetRefusal,
            "a different one-liner for the same file is still a repeat read");

        // Сборка — действие: после неё перечитывать файл можно снова.
        guard.Record("bash", """{"command":"dotnet build"}""", "ok");
        Assert(guard.Refuse("bash", """{"command":"Get-Content MainForm.cs"}""") is null,
            "after a build the file may be read again");
        return Task.CompletedTask;
    }

    // LOOP_GUARD: агент читал одни и те же куски файла без правок. Но пагинация — штатный способ
    // прочитать большой файл: на реальном прогоне агент не смог дочитать index.html, потому что
    // третий offset считался «тем же объектом».
    private static Task ReadTargetAsync()
    {
        var guard = new AgentLoopGuard();
        // Разные страницы одного файла — разная информация: это не петля.
        for (var i = 0; i < 3; i++)
        {
            var page = $"{{\"path\":\"MainForm.cs\",\"offset\":{i * 100},\"limit\":100}}";
            Assert(guard.Refuse("file_read", page) is null, $"page #{i + 1} must be allowed");
            Assert(guard.IsNewReadTarget("file_read", page), $"page #{i + 1} is new information");
            guard.Record("file_read", page, $"chunk {i}");
        }

        // Одна и та же страница снова и снова — вот это петля.
        const string first = """{"path":"MainForm.cs","offset":0,"limit":100}""";
        Assert(!guard.IsNewReadTarget("file_read", first), "the same page is not new");
        for (var i = 1; i < AgentLoopGuard.ReadTargetThreshold; i++)
        {
            Assert(guard.Refuse("file_read", first) is null, $"repeat #{i} is still allowed");
            guard.Record("file_read", first, "chunk 0");
        }
        Assert(guard.Refuse("file_read", first) == AgentLoopGuard.ReadTargetRefusal,
            "the third identical read is refused");

        // Другой файл — другой объект.
        Assert(guard.Refuse("file_read", """{"path":"TrayIcon.cs"}""") is null, "a different file is allowed");

        // Правка — прогресс: после неё файл можно читать снова.
        guard.Record("file_write", """{"path":"MainForm.cs","content":"x"}""", "ok");
        Assert(guard.Refuse("file_read", """{"path":"MainForm.cs"}""") is null,
            "after an edit the file may be read again");

        // Бюджет чтения тоже питает счётчик отказов: после трёх — принудительный итог.
        Assert(guard.RefuseRead() == AgentLoopGuard.ReadBudgetRefusal, "the read budget refusal has its own text");
        guard.RefuseRead();
        guard.RefuseRead();
        Assert(guard.ShouldStop, "three read refusals must force a final answer");
        return Task.CompletedTask;
    }

    // LOOP_GUARD: в реальном прогоне модель перебирала имена файлов (t.xlsx, t2.xlsx, …), а вывод
    // оставался тем же. Точное совпадение аргументов такое не ловит.
    // BOOKKEEPING_TOOLS: обновление плана и вопросы человеку повторяются с одинаковым ответом постоянно.
    // Раньше это выглядело как «топтание на месте» и отключало все инструменты посреди задачи.
    private static Task BookkeepingAsync()
    {
        var guard = new AgentLoopGuard();
        for (var i = 0; i < AgentLoopGuard.StopAfterRefusals + 3; i++)
        {
            var args = "{\"todos\":[{\"id\":\"" + i + "\",\"status\":\"skipped\"}]}";
            Assert(guard.Refuse("todo_write", args) is null, "the plan may always be updated");
            guard.Record("todo_write", args, "План обновлён.");
        }
        Assert(!guard.ShouldStop, "plan updates must never stop the run");
        Assert(guard.Refusals == 0, $"no refusals from bookkeeping, got {guard.Refusals}");
        Assert(guard.Refuse("ask_user", "{\"question\":\"Какой вариант?\"}") is null, "asking the user is always allowed");
        return Task.CompletedTask;
    }

    private static Task NoProgressAsync()
    {
        var guard = new AgentLoopGuard();
        for (var i = 1; i <= AgentLoopGuard.NoProgressThreshold; i++)
        {
            var args = $"{{\"command\":\"python t{i}.py\"}}";
            Assert(guard.Refuse("bash", args) is null, $"call #{i} must be allowed");
            guard.Record("bash", args, "C15 = 11350");
            // Между командами модель читала результат другим инструментом — цепочка не должна рваться.
            guard.Record("file_read", $"{{\"path\":\"out{i}.txt\"}}", "C15 = 11350");
        }

        Assert(guard.Refuse("bash", """{"command":"python t99.py"}""") == AgentLoopGuard.NoProgressRefusal,
            "the next call with the same output must be refused");
        Assert(guard.Refusals == 1 && !guard.ShouldStop, "one refusal is not enough to stop yet");

        Assert(guard.Refuse("bash", """{"command":"python t100.py"}""") == AgentLoopGuard.NoProgressRefusal, "still refused");
        Assert(guard.Refuse("bash", """{"command":"python t101.py"}""") == AgentLoopGuard.NoProgressRefusal, "still refused");
        Assert(guard.ShouldStop, $"after {AgentLoopGuard.StopAfterRefusals} refusals the runner must force a final answer");

        // Новый результат снимает петлю: это уже работа, а не топтание на месте.
        var fresh = new AgentLoopGuard();
        for (var i = 1; i <= 4; i++)
        {
            fresh.Record("bash", $"{{\"command\":\"step {i}\"}}", $"result {i}");
        }
        Assert(fresh.Refuse("bash", """{"command":"step 5"}""") is null, "changing output is progress, not a loop");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task RefusesThirdRepeatAsync()
    {
        var guard = new AgentLoopGuard();
        const string bash = """{"command":"dotnet build"}""";
        const string error = "error CS1002: ; expected";

        // Первые три вызова разрешены и запоминаются.
        for (var i = 1; i <= 3; i++)
        {
            Assert(guard.Refuse("bash", bash) is null, $"call #{i} must be allowed");
            guard.Record("bash", bash, error);
        }

        // Четвёртый — отказ, и именно текстом из ТЗ (модель читает его как результат инструмента).
        Assert(guard.Refuse("bash", bash) == AgentLoopGuard.RepeatRefusal,
            "the fourth identical call must be refused with the repeat message");

        // И отказ повторяется на каждый следующий вызов: сам отказ состояние петли не сбрасывает
        // (в раннере отказ и не записывается в историю вызовов — записываются только выполненные).
        Assert(guard.Refuse("bash", bash) == AgentLoopGuard.RepeatRefusal,
            "the refusal must keep firing while the model repeats itself");

        return Task.CompletedTask;
    }

    private static Task NotALoopAsync()
    {
        const string bash = """{"command":"dotnet build"}""";

        // Вывод меняется от попытки к попытке — это попытки, а не петля.
        var changing = new AgentLoopGuard();
        for (var i = 1; i <= 4; i++)
        {
            changing.Record("bash", bash, $"attempt {i}: new error");
        }
        Assert(changing.Refuse("bash", bash) is null, "changing output must never be treated as a loop");

        // Третий одинаковый сломан другим инструментом: цепочка рвётся, петли нет.
        var interleaved = new AgentLoopGuard();
        interleaved.Record("bash", bash, "same");
        interleaved.Record("bash", bash, "same");
        interleaved.Record("read_file", """{"path":"a.cs"}""", "ok");
        interleaved.Record("bash", bash, "same");
        Assert(interleaved.Refuse("bash", bash) is null, "another call in between must break the chain");

        // Другой аргумент — другая команда, даже если инструмент тот же.
        var otherArgs = new AgentLoopGuard();
        for (var i = 1; i <= 3; i++)
        {
            otherArgs.Record("bash", """{"command":"ls"}""", "same");
        }
        Assert(otherArgs.Refuse("bash", """{"command":"pwd"}""") is null, "a different command must not be refused");

        return Task.CompletedTask;
    }

    private static Task WebBudgetAsync()
    {
        var guard = new AgentLoopGuard();

        // Ровно бюджет обращений разрешено, следующее — отказ (бюджет — на ОДИН ход модели).
        for (var i = 1; i <= AgentLoopGuard.WebCallBudget; i++)
        {
            Assert(guard.Refuse("web_search", $"{{\"query\":\"q{i}\"}}") is null, $"web call #{i} must be allowed");
        }

        // Обращение сверх бюджета — отказ с текстом из ТЗ, и до сети дело не доходит.
        Assert(guard.Refuse("web_search", """{"query":"over"}""") == AgentLoopGuard.WebBudgetRefusal,
            "the call past the budget must be refused");
        Assert(guard.Refuse("fetch_web_page", """{"url":"https://example.com"}""") == AgentLoopGuard.WebBudgetRefusal,
            "fetch_web_page shares the same budget as web_search");
        Assert(guard.WebCallsUsed == AgentLoopGuard.WebCallBudget, $"budget accounting, got {guard.WebCallsUsed}");

        // Бюджет — на один ход, а не на задачу: следующий ход открывает его заново.
        guard.ResetWebBudget();
        Assert(guard.Refuse("web_search", """{"query":"fresh"}""") is null,
            "a new model turn must open a fresh web budget");
        Assert(guard.WebCallsUsed == 1, $"the counter restarts each turn, got {guard.WebCallsUsed}");

        // Обычные инструменты бюджет не тратят.
        var mixed = new AgentLoopGuard();
        for (var i = 0; i < 10; i++)
        {
            Assert(mixed.Refuse("bash", $"{{\"command\":\"cmd {i}\"}}") is null, "bash must not be capped by the web budget");
        }
        Assert(mixed.WebCallsUsed == 0, $"bash must not spend the web budget, got {mixed.WebCallsUsed}");

        return Task.CompletedTask;
    }

    /// <summary>Последний шаг цикла обязан просить модель подвести итог, а не молча оборвать прогон.</summary>
    private static Task WrapUpFragmentAsync()
    {
        var text = PromptFragments.FinalStepWrapUp;
        Assert(!string.IsNullOrWhiteSpace(text), "the wrap-up fragment must not be empty");
        Assert(text.Contains("Не вызывай инструменты"), "the model must be told not to call tools on the last step");
        Assert(text.Contains("итог"), "the model must be told to sum up for the user");

        return Task.CompletedTask;
    }
}

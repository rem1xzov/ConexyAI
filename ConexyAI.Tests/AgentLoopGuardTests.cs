using System.Runtime.CompilerServices;
using ConexyAI.Service;

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
        TestRegistry.Add("loop guard: web tools are capped per run, other tools do not spend the cap", WebBudgetAsync);
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
        var queries = new[] { "one", "two", "three", "four", "five" };

        foreach (var query in queries)
        {
            Assert(guard.Refuse("web_search", $"{{\"query\":\"{query}\"}}") is null, $"web call '{query}' must be allowed");
        }

        // Шестое обращение — отказ с текстом из ТЗ, и до сети дело не доходит.
        Assert(guard.Refuse("web_search", """{"query":"six"}""") == AgentLoopGuard.WebBudgetRefusal,
            "the sixth web call must hit the budget");
        Assert(guard.Refuse("fetch_web_page", """{"url":"https://example.com"}""") == AgentLoopGuard.WebBudgetRefusal,
            "fetch_web_page shares the same budget as web_search");
        Assert(guard.WebCallsUsed == AgentLoopGuard.WebCallBudget, $"budget accounting, got {guard.WebCallsUsed}");

        // Обычные инструменты бюджет не тратят.
        var mixed = new AgentLoopGuard();
        for (var i = 0; i < 10; i++)
        {
            Assert(mixed.Refuse("bash", $"{{\"command\":\"cmd {i}\"}}") is null, "bash must not be capped by the web budget");
        }
        Assert(mixed.WebCallsUsed == 0, $"bash must not spend the web budget, got {mixed.WebCallsUsed}");

        return Task.CompletedTask;
    }
}

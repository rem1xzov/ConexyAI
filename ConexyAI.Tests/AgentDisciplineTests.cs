using System.Runtime.CompilerServices;
using ConexyAI.Service;
using ConexyAI.Service.Prompts;

// AGENT_DISCIPLINE: добавлено 2026-10-20
/// <summary>
/// Правила, которые не дают агенту «пиздеть» вместо работы: не отрицать существование продукта без
/// поиска и не врать о расходе токенов (русский текст режется мельче латиницы).
/// </summary>
internal static class AgentDisciplineTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("web first: a denial without a search is caught", DenialAsync);
        TestRegistry.Add("token estimate: cyrillic is counted heavier than latin", TokensAsync);
        TestRegistry.Add("prompt: the work style requires a search before denying", PromptAsync);
        TestRegistry.Add("verify gate: a test run that found no tests is not a pass", NoTestsAsync);
    }

    // WEB_FIRST: живой случай — агент ответил «такой версии не существует», не сделав ни одного поиска.
    private static Task DenialAsync()
    {
        Assert(ConexyAgentRunner.ClaimsSomethingDoesNotExist("Qwen 3.8 не существует, последняя версия 3.5"),
            "a flat denial must be caught");
        Assert(ConexyAgentRunner.ClaimsSomethingDoesNotExist("Такой модели нет в открытых источниках"),
            "a denial phrased differently must be caught");
        Assert(ConexyAgentRunner.ClaimsSomethingDoesNotExist("Не нашёл информации об этой версии"),
            "no information is not proof of absence");
        Assert(!ConexyAgentRunner.ClaimsSomethingDoesNotExist("Поиск не дал результатов, вот что известно"),
            "a search-backed statement is fine");
        Assert(!ConexyAgentRunner.ClaimsSomethingDoesNotExist("Вот код сервера и миграции"),
            "a normal answer is not a denial");
        Assert(!ConexyAgentRunner.ClaimsSomethingDoesNotExist(null), "empty text is not a denial");
        return Task.CompletedTask;
    }

    // TOKEN_ECONOMY: кириллица в BPE режется мельче латиницы. Оценка «4 символа на токен» занижала
    // расход вдвое, и бюджет агента не срабатывал.
    private static Task TokensAsync()
    {
        var latin200 = new string('a', 200);
        var cyrillic200 = new string('я', 200);
        Assert(ConexyAgentRunner.EstimateText(latin200) == 50, $"latin 200 chars is ~50 tokens, got {ConexyAgentRunner.EstimateText(latin200)}");
        Assert(ConexyAgentRunner.EstimateText(cyrillic200) == 80, $"cyrillic 200 chars is ~80 tokens, got {ConexyAgentRunner.EstimateText(cyrillic200)}");
        Assert(ConexyAgentRunner.EstimateText(cyrillic200) > ConexyAgentRunner.EstimateText(latin200),
            "cyrillic must cost more than the same length in latin");
        Assert(ConexyAgentRunner.EstimateText("") == 0 && ConexyAgentRunner.EstimateText(null) == 0, "empty text is zero");
        return Task.CompletedTask;
    }

    private static Task PromptAsync()
    {
        var text = PromptFragments.AgentWorkStyle;
        Assert(text.Contains("web_search", StringComparison.Ordinal), "the work style must name the search tool");
        Assert(text.Contains("не существует", StringComparison.Ordinal), "the work style must forbid the denial");
        return Task.CompletedTask;
    }

    // TEST_EMPTY: «нет доступных тестов» — не успешная проверка (живой случай из Biocompass).
    private static Task NoTestsAsync()
    {
        Assert(VerificationCommands.OutputShowsNoTests("Нет доступных тестов. Проверьте, что проект содержит тесты."),
            "Russian 'no tests available' must be recognised");
        Assert(VerificationCommands.OutputShowsNoTests("No tests were found in the project."),
            "English 'no tests found' must be recognised");
        Assert(!VerificationCommands.OutputShowsNoTests("Passed! - Failed: 0, Passed: 12"),
            "a real pass is not 'no tests'");
        Assert(!VerificationCommands.OutputShowsNoTests(null), "no output is not a match");

        var health = new BuildHealth();
        health.Observe("dotnet test", true, "Нет доступных тестов.");
        Assert(health.AnyVerificationRan && health.NoTestsFound && !health.IsRed,
            "an empty test run ran but proves nothing");
        health.Observe("dotnet test", true, "Passed! - Failed: 0, Passed: 12");
        Assert(!health.NoTestsFound, "a real test run clears the flag");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);
}

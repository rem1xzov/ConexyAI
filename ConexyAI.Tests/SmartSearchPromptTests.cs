using System.Runtime.CompilerServices;
using ConexyAI.Service.Prompts;

// SMART_SEARCH_DEFAULT: добавлено 2026-09-29
/// <summary>
/// Умный поиск включён по умолчанию в чатах (flash/pro), поэтому фрагмент закрепляет порядок работы:
/// сначала собственные знания, web_search — только для факт-чекинга того, в чём модель не уверена, и
/// никаких выдуманных фактов. Логика чистая (только строка), поэтому проверяется напрямую.
/// </summary>
internal static class SmartSearchPromptTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("smart search: knowledge first, search only for fact-checking", KnowledgeFirstAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task KnowledgeFirstAsync()
    {
        var text = PromptFragments.SmartSearch;
        Assert(text.Contains("web_search"), "the fragment must name the web_search tool");
        Assert(text.Contains("собственные знания"), "the model must be told to use its own knowledge first");
        Assert(text.Contains("не выдумывай факты"), "the model must be told never to invent facts");
        Assert(text.Contains("не уверен"), "an uncertain fact must be admitted, not guessed");
        Assert(text.Contains("проверю в интернете"), "the model must say it will check the web instead of guessing");
        return Task.CompletedTask;
    }
}

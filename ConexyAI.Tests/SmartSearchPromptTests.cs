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
        TestRegistry.Add("smart search: fresh/versioned questions force a web search", FreshInfoAsync);
        TestRegistry.Add("smart search: denying existence from memory is forbidden", DenialRuleAsync);
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

    private static Task FreshInfoAsync()
    {
        Assert(PromptFragments.RequiresFreshInfo("Расскажи про DeepSeek V4.1 Flash"), "a versioned model name forces fresh info");
        Assert(PromptFragments.RequiresFreshInfo("Какая сейчас цена подписки?"), "a price question forces fresh info");
        Assert(PromptFragments.RequiresFreshInfo("Что нового вышло в 2026?"), "a recent-release question forces fresh info");
        Assert(!PromptFragments.RequiresFreshInfo("Напиши функцию на C#"), "ordinary code work must not force a search");
        Assert(!PromptFragments.RequiresFreshInfo("Что такое рекурсия?"), "a stable definition must not force a search");
        return Task.CompletedTask;
    }

    private static Task DenialRuleAsync()
    {
        var text = PromptFragments.SmartSearch;
        Assert(text.Contains("не существует"), "the fragment must forbid denying existence");
        Assert(text.Contains("только потому, что ты об этом не помнишь"), "denying from memory must be called out explicitly");
        return Task.CompletedTask;
    }
}

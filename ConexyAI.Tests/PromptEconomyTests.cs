using System.Runtime.CompilerServices;
using ConexyAI.Service;

// TOKEN_ECONOMY: добавлено 2026-10-01
/// <summary>
/// Решение о дорогих блоках контекста (память, персонализация, другие чаты). Логика чистая, поэтому
/// проверяется напрямую: короткая реплика без личного запроса — блоки не нужны, всё остальное — нужны.
/// </summary>
internal static class PromptEconomyTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("economy: a short greeting skips the expensive context", GreetingAsync);
        TestRegistry.Add("economy: personal or long requests keep the context", ContextNeededAsync);
        TestRegistry.Add("economy: only exact greetings count as small talk", SmallTalkAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task GreetingAsync()
    {
        Assert(!PromptEconomy.NeedsAuxiliaryContext("привет"), "a bare greeting must not pull memory/preferences");
        Assert(!PromptEconomy.NeedsAuxiliaryContext("Привет!"), "case and punctuation must not matter");
        Assert(!PromptEconomy.NeedsAuxiliaryContext("спасибо, понятно"), "small talk must skip the blocks");
        Assert(!PromptEconomy.NeedsAuxiliaryContext("исправь баг в auth.ts"), "a short non-personal request skips them");
        Assert(!PromptEconomy.NeedsAuxiliaryContext(null), "no message means no context");
        Assert(!PromptEconomy.NeedsAuxiliaryContext("   "), "blank means no context");
        return Task.CompletedTask;
    }

    private static Task ContextNeededAsync()
    {
        Assert(PromptEconomy.NeedsAuxiliaryContext("что ты обо мне знаешь?"), "a personal question needs the blocks");
        Assert(PromptEconomy.NeedsAuxiliaryContext("напомни, что мы обсуждали"), "recall needs the blocks");
        Assert(PromptEconomy.NeedsAuxiliaryContext("сделай как раньше"), "a 'like before' hint needs the blocks");
        Assert(
            PromptEconomy.NeedsAuxiliaryContext(new string('а', PromptEconomy.TrivialMaxChars + 1)),
            "a long request keeps the blocks");
        return Task.CompletedTask;
    }

    private static Task SmallTalkAsync()
    {
        Assert(PromptEconomy.IsSmallTalk("привет"), "a bare greeting is small talk");
        Assert(PromptEconomy.IsSmallTalk("Привет!"), "punctuation must not stop the match");
        Assert(PromptEconomy.IsSmallTalk("hello"), "english greetings count too");
        Assert(PromptEconomy.IsSmallTalk("как дела?"), "small talk counts");

        // Настоящие задачи НЕ должны попадать в короткий режим, даже с приветствием в начале.
        Assert(!PromptEconomy.IsSmallTalk("привет, сделай тетрис"), "a greeting plus a task is not small talk");
        Assert(!PromptEconomy.IsSmallTalk("исправь баг в auth.ts"), "a task is not small talk");
        Assert(!PromptEconomy.IsSmallTalk("здравствуйте, коллеги"), "unknown phrasings keep the full mode");
        Assert(!PromptEconomy.IsSmallTalk(null), "no message is not small talk");
        return Task.CompletedTask;
    }
}

using System.Runtime.CompilerServices;
using ConexyAI.Service.Prompts;

// CONTENT_POLICY: добавлено 2026-09-29
// TOKEN_ECONOMY: изменено 2026-10-06 — политика разделена: базовая (всем режимам) и кризисная
// (только режимы живого диалога). Агенты Coder/Cowork получают только базовую часть.
// CENSORSHIP_OFF: изменено 2026-10-26 — своих запретов у сервиса больше нет, кроме политики:
// остальное оставлено провайдеру модели.
/// <summary>
/// Контент-политика: базовая часть (свобода на взрослые и острые темы + единственное изъятие —
/// политика) идёт всем режимам, кризисный блок с телефонами — только режимам живого диалога.
/// </summary>
internal static class ContentPolicyPromptTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("content policy: free speech with a politics-only limit", FreeSpeechWithLimitsAsync);
        TestRegistry.Add("content policy: crisis support and helplines", CrisisSupportAsync);
        TestRegistry.Add("content policy: the crisis block is separate from the core rules", CrisisIsSeparateAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task FreeSpeechWithLimitsAsync()
    {
        var text = PromptFragments.ContentPolicy;
        Assert(text.Contains("Контент-политика"), "the fragment must be named");
        // Freedom: keeps the model from blanket refusals.
        Assert(text.Contains("без ханжества"), "the model must be told to drop prudishness");
        Assert(text.Contains("18+"), "adult topics must be allowed");
        // CENSORSHIP_OFF: всё, кроме политики, решает провайдер.
        Assert(text.Contains("провайдер"), "the provider owns the remaining limits");
        Assert(text.Contains("Политика") && text.Contains("не участвуешь в политических дискуссиях"), "politics must be refused with a clear line");
        Assert(!text.Contains("Преступления"), "the service must not keep its own crime rules");
        Assert(!text.Contains("взрывчат"), "the service must not keep weapon rules");
        Assert(!text.Contains("наркотик"), "the service must not keep drug rules");
        // TOKEN_ECONOMY: кризисные телефоны больше НЕ в базовой части — они в отдельном фрагменте.
        Assert(!text.Contains("Телефоны доверия"), "the core policy must not carry the helpline list");
        return Task.CompletedTask;
    }

    private static Task CrisisSupportAsync()
    {
        var text = PromptFragments.ContentPolicyCrisis;
        Assert(text.Contains("суицид"), "suicide must be handled explicitly");
        Assert(text.Contains("как друг"), "the model must listen like a friend");
        Assert(text.Contains("специалисту"), "it must point to a specialist");
        Assert(text.Contains("Телефоны доверия"), "helplines must be listed");
        // One line per region the product speaks to.
        foreach (var marker in new[] { "333-44-34", "133", "150", "1246", "112" })
        {
            Assert(text.Contains(marker), $"helplines must include {marker}");
        }
        return Task.CompletedTask;
    }

    /// <summary>Агентские режимы берут только базу: без телефонов и без ProductKnowledge.</summary>
    private static Task CrisisIsSeparateAsync()
    {
        Assert(!PromptFragments.ContentPolicy.Contains("Телефоны доверия"),
            "the core policy stays free of the crisis list so agents can take it alone");
        Assert(PromptFragments.ContentPolicyCrisis.Contains("Телефоны доверия"),
            "the crisis fragment carries the helplines for the dialog modes");
        Assert(PromptFragments.AudienceLine(true, "Pro").Contains("администратор"),
            "the short audience line must still name an admin");
        Assert(PromptFragments.AudienceLine(false, "Free").Contains("Free"),
            "the short audience line must still name the tier");
        // AGENT_SAFETY: у агента вместо полной политики — одна строка; своих запретов сверх
        // политики провайдера в ней нет (CENSORSHIP_OFF).
        Assert(PromptFragments.AgentSafetyLine.Contains("Политическ"),
            "the agent safety line keeps the politics rule");
        Assert(PromptFragments.AgentSafetyLine.Contains("провайдер"),
            "the agent safety line defers everything else to the provider");
        Assert(!PromptFragments.AgentSafetyLine.Contains("оружия") && !PromptFragments.AgentSafetyLine.Contains("наркотиков"),
            "the agent safety line must not carry its own censorship");
        return Task.CompletedTask;
    }
}

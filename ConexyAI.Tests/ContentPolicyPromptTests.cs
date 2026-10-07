using System.Runtime.CompilerServices;
using ConexyAI.Service.Prompts;

// CONTENT_POLICY: добавлено 2026-09-29
// TOKEN_ECONOMY: изменено 2026-10-06 — политика разделена: базовая (всем режимам) и кризисная
// (только режимы живого диалога). Агенты Coder/Cowork получают только базовую часть.
/// <summary>
/// Контент-политика: базовая часть (свобода на взрослые и острые темы + жёсткие изъятия) идёт всем
/// режимам, кризисный блок с телефонами — только режимам живого диалога. Проверяем обе.
/// </summary>
internal static class ContentPolicyPromptTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("content policy: free speech with hard limits", FreeSpeechWithLimitsAsync);
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
        // Limits.
        Assert(text.Contains("Преступления") && text.Contains("наказуемы"), "crimes must be warned against");
        Assert(text.Contains("взрывчат"), "weapon/explosive instructions must be banned");
        Assert(text.Contains("наркотик"), "drugs must be off-limits");
        Assert(text.Contains("Политика") && text.Contains("не участвуешь в политических дискуссиях"), "politics must be refused with a clear line");
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
        return Task.CompletedTask;
    }
}

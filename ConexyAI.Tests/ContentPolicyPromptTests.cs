using System.Runtime.CompilerServices;
using ConexyAI.Service.Prompts;

// CONTENT_POLICY: добавлено 2026-09-29
/// <summary>
/// Единая контент-политика дописывается к промпту всех режимов, поэтому проверяем, что в ней есть
/// свобода на взрослые и острые темы, но сохранены жёсткие изъятия (преступления, оружие, наркотики,
/// политика) и обязательная поддержка при суицидальных мыслях с телефонами доверия.
/// </summary>
internal static class ContentPolicyPromptTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("content policy: free speech with hard limits", FreeSpeechWithLimitsAsync);
        TestRegistry.Add("content policy: crisis support and helplines", CrisisSupportAsync);
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
        Assert(text.Contains("Оружие и взрывчатка"), "weapon/explosive instructions must be banned");
        Assert(text.Contains("Наркотики"), "drugs must be off-limits");
        Assert(text.Contains("Политика") && text.Contains("не участвуешь в политических дискуссиях"), "politics must be refused with a clear line");
        Assert(text.Contains("Россия/Украина"), "the politics rule must name the obvious example");
        return Task.CompletedTask;
    }

    private static Task CrisisSupportAsync()
    {
        var text = PromptFragments.ContentPolicy;
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
}

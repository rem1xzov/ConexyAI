using System.Runtime.CompilerServices;
using ConexyAI.Service.Prompts;

// USER_CONTEXT / PRODUCT_KNOWLEDGE: добавлено 2026-09-27
/// <summary>
/// Блоки промпта, по которым модель понимает экосистему ConexyAI и то, кто именно с ней общается.
/// Логика чистая (только строки), поэтому проверяется напрямую, без воркера и БД.
/// </summary>
internal static class UserContextPromptTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("prompt: admin audience block says full access and Cowork availability", AdminAudienceAsync);
        TestRegistry.Add("prompt: free user is told Cowork is unavailable", FreeAudienceAsync);
        TestRegistry.Add("prompt: paid tier unlocks Cowork", PaidAudienceAsync);
        TestRegistry.Add("prompt: product knowledge names the modes and the tiers", ProductKnowledgeAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task AdminAudienceAsync()
    {
        var text = PromptFragments.AudienceContext(isAdmin: true, tier: "Admin");
        Assert(text.Contains("администратор"), "admin must be named as an administrator");
        Assert(text.Contains("лимиты на него не действуют"), "admin must be told there are no limits");
        Assert(text.Contains("Cowork ему доступен"), "admin always has Cowork");
        return Task.CompletedTask;
    }

    private static Task FreeAudienceAsync()
    {
        var text = PromptFragments.AudienceContext(isAdmin: false, tier: "Free");
        Assert(text.Contains("Free"), $"free tier must be named, got: {text}");
        Assert(text.Contains("Cowork ему недоступен"), "free tier must not be promised Cowork");
        return Task.CompletedTask;
    }

    private static Task PaidAudienceAsync()
    {
        var pro = PromptFragments.AudienceContext(isAdmin: false, tier: "Pro");
        Assert(pro.Contains("тариф Pro"), $"Pro must be named, got: {pro}");
        Assert(pro.Contains("Cowork ему доступен"), "Pro must unlock Cowork");

        // Неизвестный/пустой тариф не должен случайно открывать Cowork.
        var unknown = PromptFragments.AudienceContext(isAdmin: false, tier: null);
        Assert(unknown.Contains("Cowork ему недоступен"), "an unknown tier must fall back to no Cowork");

        return Task.CompletedTask;
    }

    private static Task ProductKnowledgeAsync()
    {
        var text = PromptFragments.ProductKnowledge;
        foreach (var marker in new[] { "Чат", "Ученики", "Coder", "Cowork", "ConexyV1-flash", "Free", "ProMax", "Ultra" })
        {
            Assert(text.Contains(marker), $"product knowledge must mention '{marker}'");
        }
        // AGENT_FREE_ACCESS: Coder и его рабочая область доступны на всех тарифах, у Free свой
        // лимит токенов. Прежняя формулировка («агентские режимы ... входят в платные тарифы»)
        // заставляла агента на бесплатном тарифе говорить, что доступа к рабочей области нет.
        Assert(text.Contains("включая Free"), "Coder must be stated as available on Free");
        Assert(text.Contains("платным является только Cowork"), "only Cowork must be paid-only");
        Assert(!text.Contains("агентские режимы и Cowork входят в платные тарифы"),
            "the old paid-only agent wording must be gone");
        return Task.CompletedTask;
    }
}

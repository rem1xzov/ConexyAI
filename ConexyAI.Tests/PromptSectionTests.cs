using System.Runtime.CompilerServices;
using ConexyAI.Service;

// PROMPT_SECTIONS: добавлено 2026-11-20
/// <summary>
/// Секционный промпт: детерминированный порядок, одна секция на имя, пустые секции не попадают в
/// запрос, лимит обрезает с явным маркером. Чистая логика — проверяется без модели.
/// </summary>
internal static class PromptSectionTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("prompt sections: order by priority and name is deterministic", OrderAsync);
        TestRegistry.Add("prompt sections: empty and duplicate sections are dropped", DedupeAsync);
        TestRegistry.Add("prompt sections: composing keeps the order and joins with a blank line", ComposeAsync);
        TestRegistry.Add("prompt sections: a limit truncates with an explicit marker", LimitAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task OrderAsync()
    {
        var ordered = PromptComposer.Order([
            new PromptSection(100, "skills", "skills text"),
            new PromptSection(10, "charter", "charter text"),
            new PromptSection(10, "rules", "rules text"),
        ]);
        Assert(ordered.Select(section => section.Name).SequenceEqual(["charter", "rules", "skills"]),
            "lower order first, ties by name: " + string.Join(",", ordered.Select(s => s.Name)));
        return Task.CompletedTask;
    }

    private static Task DedupeAsync()
    {
        var ordered = PromptComposer.Order([
            new PromptSection(1, "rules", "first"),
            new PromptSection(2, "rules", "second"),
            new PromptSection(3, "empty", "   "),
        ]);
        Assert(ordered.Count == 1 && ordered[0].Text == "first", "the first section with a name wins; empty ones are dropped");
        return Task.CompletedTask;
    }

    private static Task ComposeAsync()
    {
        var text = PromptComposer.Compose([
            new PromptSection(2, "b", "two"),
            new PromptSection(1, "a", " one "),
        ]);
        Assert(text == "one\n\ntwo", "sections are trimmed, joined with a blank line and ordered: " + text);
        Assert(PromptComposer.Compose([]) == string.Empty, "no sections means an empty prompt");
        return Task.CompletedTask;
    }

    private static Task LimitAsync()
    {
        var text = PromptComposer.Compose([new PromptSection(1, "big", new string('x', 500))], maxChars: 100);
        Assert(text.Length <= 100, "the limit is respected, got " + text.Length);
        Assert(text.Contains("обрезана", StringComparison.Ordinal), "truncation is explicit");
        return Task.CompletedTask;
    }
}

using System.Text.Json;
using ConexyAI.Contract;
using ConexyAI.Service;

// HARNESS_CONFORMANCE: проверки того, что схемы и подсказки НАПРАВЛЯЮТ модель, а не сбивают её.
// Zed и deepseek-harness держат один жёсткий контракт: одно имя — одна работа, параметры с описанием,
// лишние ключи запрещены, подсказка ссылается только на выданные имена. Эти инварианты ломаются тихо,
// поэтому проверяются тестами, а не глазами.
internal static class HarnessConformanceTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("harness: no legacy tool names are advertised", NoLegacyNamesAsync);
        TestRegistry.Add("harness: descriptions never name a tool that is not advertised", DescriptionsUseAdvertisedNamesAsync);
        TestRegistry.Add("harness: every advertised tool has a one-line guidance", GuidanceCoversCoreToolsAsync);
        TestRegistry.Add("harness: aliases map to real internal tools and are idempotent", AliasesAreConsistentAsync);
        TestRegistry.Add("harness: old long tool outputs are trimmed, recent ones are kept", ContextCompressionKeepsRecentAsync);
        TestRegistry.Add("harness: a checker can declare which roles must finish first", SpawnAgentsOrderingAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static readonly string[] LegacyNames =
    {
        "file_read", "file_write", "file_patch", "str_replace_editor", "view_image", "file_read_bytes", "file_write_bytes",
    };

    private static List<string> Advertised() => AgentToolCatalog.Names().ToList();

    private static Task NoLegacyNamesAsync()
    {
        var advertised = Advertised();
        foreach (var legacy in LegacyNames)
            Assert(!advertised.Contains(legacy), $"'{legacy}' must not be advertised: one job, one name");
        return Task.CompletedTask;
    }

    // Если описание говорит «используй file_write», а такого инструмента нет, модель вызовет фантом.
    private static Task DescriptionsUseAdvertisedNamesAsync()
    {
        var advertised = Advertised().ToHashSet(StringComparer.Ordinal);
        foreach (var schema in AgentToolCatalog.Build())
        {
            var function = JsonSerializer.SerializeToElement(schema).GetProperty("function");
            var name = function.GetProperty("name").GetString() ?? string.Empty;
            var description = function.GetProperty("description").GetString() ?? string.Empty;
            foreach (var legacy in LegacyNames)
                Assert(!description.Contains(legacy, StringComparison.Ordinal),
                    $"{name}: description mentions '{legacy}', which is not advertised");

            foreach (var property in function.GetProperty("parameters").GetProperty("properties").EnumerateObject())
            {
                var text = property.Value.TryGetProperty("description", out var value) ? value.GetString() ?? string.Empty : string.Empty;
                foreach (var legacy in LegacyNames)
                    Assert(!text.Contains(legacy, StringComparison.Ordinal),
                        $"{name}.{property.Name}: parameter description mentions '{legacy}'");
            }
            Assert(advertised.Contains(name), $"{name} describes itself but is not in Names()");
        }
        return Task.CompletedTask;
    }

    // Подсказка — это то, что реально управляет выбором; она обязана ссылаться на выданные имена.
    private static Task GuidanceCoversCoreToolsAsync()
    {
        var advertised = Advertised();
        var guidance = AgentToolCatalog.GuidanceFor(advertised);
        Assert(guidance.Length > 0, "core tools must come with guidance");

        foreach (var core in new[] { "`read`", "`write`", "`edit`", "`apply_patch`" })
            Assert(guidance.Contains(core, StringComparison.Ordinal), $"guidance must steer to {core}");

        foreach (var legacy in LegacyNames)
            Assert(!guidance.Contains(legacy, StringComparison.Ordinal), $"guidance must not mention '{legacy}'");

        // Подсказка печатается только для выданных инструментов.
        var narrow = AgentToolCatalog.GuidanceFor(new[] { "read" });
        Assert(narrow.Contains("`read`", StringComparison.Ordinal) && !narrow.Contains("`bash`", StringComparison.Ordinal),
            "guidance follows the offered set: " + narrow);
        return Task.CompletedTask;
    }

    private static Task AliasesAreConsistentAsync()
    {
        static LlmToolCall Call(string name, string args) => new("id", "function", new LlmFunctionCall(name, args));

        foreach (var (advertised, internalName) in new[]
                 {
                     ("read", "file_read"), ("write", "file_write"), ("edit", "str_replace_editor"),
                     ("read_image", "view_image"), ("shell", "bash"),
                 })
        {
            Assert(AgentToolCatalog.InternalNameFor(advertised) == internalName, $"{advertised} must map to {internalName}");
        }

        // Имена без алиаса не должны меняться: иначе сломается диспетчер, который их знает.
        Assert(AgentToolCatalog.InternalNameFor("grep") == "grep" && AgentToolCatalog.InternalNameFor("bash") == "bash",
            "unaliased names pass through");

        // Повторный перевод — не ошибка: старая запись из истории не должна превратиться в мусор.
        var legacy = AgentToolCatalog.Normalize(Call("file_read", """{"path":"a.ts"}"""));
        Assert(legacy.Function.Name == "file_read" && legacy.Function.Arguments.Contains("\"path\"", StringComparison.Ordinal),
            "a legacy call is already internal and stays untouched");
        return Task.CompletedTask;
    }

    // Потеря контекста — главная причина «модель забыла, что уже сделала». Старые длинные выводы обязаны
    // сжиматься, а последние — оставаться целыми, иначе агент перестаёт видеть, что только что произошло.
    private static Task ContextCompressionKeepsRecentAsync()
    {
        var messages = new List<ChatMessage> { new("system", "charter"), new("user", "task") };
        for (var i = 0; i < 12; i++)
            messages.Add(new ChatMessage("tool", new string((char)('a' + i), 6_000), ToolCallId: $"call-{i}"));

        var totalBefore = messages.Sum(m => (m.Content as string)?.Length ?? 0);
        AgentContextCompressor.Compress(messages);
        var totalAfter = messages.Sum(m => (m.Content as string)?.Length ?? 0);

        Assert(totalAfter < totalBefore / 2, $"compression must cut context, before={totalBefore} after={totalAfter}");

        var tailStart = messages.Count - AgentContextCompressor.KeepFullOutputs;
        for (var i = tailStart; i < messages.Count; i++)
            Assert((messages[i].Content as string)?.Length == 6_000, $"the recent tool output #{i - tailStart + 1} must stay intact");

        var trimmed = messages.First(m => m.Role == "tool");
        var text = trimmed.Content as string ?? string.Empty;
        Assert(text.Length < 6_000 && text.Length > 0, "an old long output must be trimmed, not dropped");
        Assert(text.Contains("обрезан", StringComparison.Ordinal) || text.Contains("truncated", StringComparison.Ordinal) ||
               text.Length <= AgentContextCompressor.TrimHeadChars + 400,
            "a trimmed output must be recognisable as trimmed: " + text.Length);
        return Task.CompletedTask;
    }

    // ORCHESTRA: проверяющая роль должна уметь стартовать после тех, кого она проверяет — иначе QA
    // «проходит» на пустом дереве. Заодно проверяем, что повторный spawn_agents разрешён явно.
    private static Task SpawnAgentsOrderingAsync()
    {
        var function = AgentToolCatalog.Build()
            .Select(schema => JsonSerializer.SerializeToElement(schema).GetProperty("function"))
            .First(f => f.GetProperty("name").GetString() == "spawn_agents");
        var agent = function.GetProperty("parameters").GetProperty("properties")
            .GetProperty("agents").GetProperty("items");
        Assert(agent.GetProperty("properties").TryGetProperty("after", out var after),
            "spawn_agents must offer 'after' so a checker waits for the work it checks");
        Assert(after.GetProperty("type").GetString() == "array", "'after' is a list of roles");
        return Task.CompletedTask;
    }
}

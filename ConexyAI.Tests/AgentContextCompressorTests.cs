using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Service;

// TOKEN_ECONOMY: добавлено 2026-10-06
/// <summary>
/// Сжатие контекста агентского прогона: свежие выводы инструментов не трогаем, старые и длинные —
/// обрезаем. Логика чистая (только список сообщений), поэтому проверяется напрямую.
/// </summary>
internal static class AgentContextCompressorTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("context compressor: old long tool outputs are trimmed, fresh ones kept", TrimsOldKeepsFreshAsync);
        TestRegistry.Add("context compressor: short outputs and non-tool messages are left alone", ShortAndNonToolUntouchedAsync);
        TestRegistry.Add("context compressor: exactly the kept count is never trimmed", BoundaryAsync);
        TestRegistry.Add("context compressor: trims in batches so the cached prefix stays stable between steps", BatchKeepsPrefixAsync);
        TestRegistry.Add("context compressor: no window pressure means no trimming at all", PressureGateAsync);
        TestRegistry.Add("context compressor: pressure trims every old output and keeps its head and tail", PressureTrimsAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static ChatMessage Tool(string text) => new("tool", text, ToolCallId: "call_" + Guid.NewGuid().ToString("N"));

    private static async Task TrimsOldKeepsFreshAsync()
    {
        await Task.CompletedTask;
        var longText = new string('x', 10_000);
        var messages = new List<ChatMessage> { new("system", "prompt"), new("user", "task") };
        for (var i = 0; i < 16; i++) messages.Add(Tool(longText));

        AgentContextCompressor.Compress(messages);

        // Всего 16 выводов, 8 остаются целыми → первые 8 (одна пачка) сжаты.
        for (var i = 0; i < 8; i++)
        {
            var content = (string)messages[2 + i].Content!;
            Assert(content.Length < longText.Length, $"output #{i} must be shortened");
            Assert(content.Contains("обрезан для экономии"), $"output #{i} must carry the truncation marker");
        }
        for (var i = 8; i < 16; i++)
        {
            var content = (string)messages[2 + i].Content!;
            Assert(content.Length == longText.Length, $"output #{i} is among the fresh ones and must stay intact");
        }
    }

    private static async Task ShortAndNonToolUntouchedAsync()
    {
        await Task.CompletedTask;
        var messages = new List<ChatMessage> { new("system", "prompt") };
        for (var i = 0; i < 12; i++) messages.Add(Tool("short output " + i));
        messages.Add(new("assistant", new string('y', 20_000)));
        messages.Add(new("user", new string('z', 20_000)));

        AgentContextCompressor.Compress(messages);

        for (var i = 0; i < 12; i++)
        {
            Assert((string)messages[1 + i].Content! == $"short output {i}", $"short output #{i} must stay intact");
        }
        // Сообщения не-инструментов не сжимаются, даже будучи длинными.
        Assert(((string)messages[13].Content!).Length == 20_000, "an assistant message must not be trimmed");
        Assert(((string)messages[14].Content!).Length == 20_000, "a user message must not be trimmed");
    }

    private static async Task BatchKeepsPrefixAsync()
    {
        await Task.CompletedTask;
        var longText = new string('x', 10_000);
        var messages = new List<ChatMessage> { new("system", "prompt") };
        // Начинаем с одного лишнего вывода: пока не наберётся целая пачка, трогать ничего нельзя.
        for (var i = 0; i < AgentContextCompressor.KeepFullOutputs + 1; i++) messages.Add(Tool(longText));

        // После каждого нового вывода до заполнения пачки старые сообщения не должны меняться.
        for (var step = 0; step < AgentContextCompressor.TrimBatch - 1; step++)
        {
            var before = messages.Select(m => (string?)m.Content).ToList();
            AgentContextCompressor.Compress(messages);
            Assert(messages.Select(m => (string?)m.Content).SequenceEqual(before),
                $"step {step}: nothing may change until a whole batch is due, or the cache prefix breaks every step");
            messages.Add(Tool(longText));
        }
    }

    private static async Task PressureGateAsync()
    {
        await Task.CompletedTask;
        var longText = new string('x', 10_000);
        var messages = new List<ChatMessage> { new("system", "prompt") };
        for (var i = 0; i < 16; i++) messages.Add(Tool(longText));

        // Огромное окно: давления нет — историю не трогаем (кэш префикса стабилен).
        var result = AgentContextCompressor.Compress(messages, contextWindowTokens: 10_000_000, reservedTokens: 16_000);
        Assert(!result.Applied && result.TrimmedOutputs == 0, "with room in the window nothing is trimmed");
        Assert(messages.All(m => m.Role != "tool" || ((string)m.Content!).Length == longText.Length),
            "no output content changes under low pressure");
    }

    private static async Task PressureTrimsAsync()
    {
        await Task.CompletedTask;
        var text = "HEAD" + new string('x', 10_000) + "TAIL";
        var messages = new List<ChatMessage> { new("system", "prompt") };
        for (var i = 0; i < 16; i++) messages.Add(Tool(text));

        // Символов ~170k (~42k токенов) при доступном бюджете 36k и пороге 0.8 — контекст под давлением.
        var result = AgentContextCompressor.Compress(messages, contextWindowTokens: 40_000, reservedTokens: 4_000);
        Assert(result.Applied, "a small window triggers compression");
        Assert(result.EstimatedTokensAfter < result.EstimatedTokensBefore, "the token estimate shrinks");

        for (var i = 0; i < 12; i++)
        {
            var content = (string)messages[1 + i].Content!;
            Assert(content.StartsWith("HEAD", StringComparison.Ordinal), $"output #{i} keeps its head");
            Assert(content.EndsWith("TAIL", StringComparison.Ordinal), $"output #{i} keeps its tail (build/test errors live there)");
            Assert(content.Contains("обрезан для экономии", StringComparison.Ordinal), $"output #{i} carries the truncation marker");
        }
        for (var i = 12; i < 16; i++)
        {
            Assert(((string)messages[1 + i].Content!).Length == text.Length, $"fresh output #{i} is untouched");
        }
    }

    private static async Task BoundaryAsync()
    {
        await Task.CompletedTask;
        var longText = new string('x', 10_000);
        var messages = new List<ChatMessage>();
        for (var i = 0; i < AgentContextCompressor.KeepFullOutputs; i++) messages.Add(Tool(longText));

        AgentContextCompressor.Compress(messages);

        Assert(messages.All(m => ((string)m.Content!).Length == longText.Length),
            "with exactly the kept count, nothing must be trimmed");
    }
}

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
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static ChatMessage Tool(string text) => new("tool", text, ToolCallId: "call_" + Guid.NewGuid().ToString("N"));

    private static async Task TrimsOldKeepsFreshAsync()
    {
        await Task.CompletedTask;
        var longText = new string('x', 10_000);
        var messages = new List<ChatMessage> { new("system", "prompt"), new("user", "task") };
        for (var i = 0; i < 12; i++) messages.Add(Tool(longText));

        AgentContextCompressor.Compress(messages);

        // Всего 12 выводов, 8 остаются целыми → первые 4 сжаты.
        for (var i = 0; i < 4; i++)
        {
            var content = (string)messages[2 + i].Content!;
            Assert(content.Length < longText.Length, $"output #{i} must be shortened");
            Assert(content.Contains("обрезан для экономии"), $"output #{i} must carry the truncation marker");
        }
        for (var i = 4; i < 12; i++)
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

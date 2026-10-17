using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Service;

// AUTO_COMPACT: добавлено 2026-10-18
/// <summary>Пересказ длинного прогона: граница не рвёт пары assistant(tool_calls) → tool, хвост цел.</summary>
internal static class AgentHistoryCompactorTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("auto compact: a small run is left alone", SmallRunAsync);
        TestRegistry.Add("auto compact: the middle is replaced, tool pairs and the tail stay valid", LongRunAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static List<ChatMessage> Run(int steps, int outputChars)
    {
        var messages = new List<ChatMessage> { new("system", "устав"), new("user", "задача") };
        for (var i = 0; i < steps; i++)
        {
            var id = "call_" + i;
            messages.Add(new ChatMessage("assistant", "шаг " + i,
                [new LlmToolCall(id, "function", new LlmFunctionCall("bash", "{\"command\":\"build\"}"))]));
            messages.Add(new ChatMessage("tool", new string('x', outputChars), ToolCallId: id));
        }
        return messages;
    }

    private static async Task SmallRunAsync()
    {
        await Task.CompletedTask;
        Assert(AgentHistoryCompactor.FindRange(Run(10, 1_000), runStart: 2) is null, "a run under the threshold must not be compacted");
    }

    private static async Task LongRunAsync()
    {
        await Task.CompletedTask;
        var messages = Run(60, 8_000);
        var range = AgentHistoryCompactor.FindRange(messages, runStart: 2);
        Assert(range is not null, "a run over the threshold must be compacted");
        var (start, end) = range!.Value;
        Assert(start == 2, "the charter and the task are never compacted");
        Assert(messages[end].Role == "assistant", "the cut must sit right before an assistant step, never between a call and its result");

        var tailBefore = messages.Skip(end).ToList();
        AgentHistoryCompactor.Apply(messages, start, end, "пересказ");
        Assert(messages[2].Role == "user" && ((string)messages[2].Content!).Contains("пересказ"), "the summary replaces the middle");
        Assert(messages.Skip(3).SequenceEqual(tailBefore), "the recent steps stay exactly as they were");
        Assert(messages.Count(m => m.Role == "tool") == messages.Count(m => m.ToolCalls is { Count: > 0 }),
            "every remaining tool result still has its call");
    }
}

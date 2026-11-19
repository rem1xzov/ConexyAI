using System.Runtime.CompilerServices;
using System.Text.Json;
using ConexyAI.Service;

// LOCAL_WORKER: добавлено 2026-11-19
/// <summary>
/// Локальная модель как субагент облачного ведущего. Сервер до модели на ПК не достаёт, поэтому
/// запрос и ответ идут через устройство; здесь проверяется разбор ответа и подмена тела под локальный сервер.
/// </summary>
internal static class LocalWorkerTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("local worker: a chat.completion answer becomes deltas with text, tools and usage", BridgeAsync);
        TestRegistry.Add("local worker: a non-JSON answer is not lost", BridgeFallbackAsync);
        TestRegistry.Add("local worker: the request body is rewritten for the local server", PrepareBodyAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task BridgeAsync()
    {
        const string body = """
            {
              "model": "qwen2.5-coder:7b",
              "choices": [{
                "message": {
                  "role": "assistant",
                  "content": "Готово.",
                  "tool_calls": [{ "id": "c1", "type": "function", "function": { "name": "file_read", "arguments": "{\"path\":\"a.cs\"}" } }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "total_tokens": 42, "prompt_tokens": 30, "completion_tokens": 12, "prompt_tokens_details": { "cached_tokens": 10 } }
            }
            """;

        var deltas = LocalChatBridge.ToDeltas(body);
        Assert(deltas.Count > 0, "the answer must produce deltas");
        Assert(deltas.Any(delta => delta.Content == "Готово."), "the text is forwarded");
        Assert(deltas.Any(delta => delta.ToolCalls is { Count: 1 } calls && calls[0].Function.Name == "file_read"),
            "tool calls are forwarded so the helper can act");
        var last = deltas[^1];
        Assert(last.FinishReason == "tool_calls", $"the finish reason is kept, got {last.FinishReason}");
        Assert(last.TotalTokens == 42, $"usage total is kept, got {last.TotalTokens}");
        Assert(last.Usage is { CachedInput: 10, Output: 12 }, "priced usage is mapped (cached input and output)");
        return Task.CompletedTask;
    }

    private static Task BridgeFallbackAsync()
    {
        var deltas = LocalChatBridge.ToDeltas("plain text answer");
        Assert(deltas.Count == 1 && deltas[0].Content == "plain text answer", "a non-JSON body is still an answer");
        Assert(LocalChatBridge.ToDeltas("").Count == 1, "an empty body ends the turn instead of crashing");
        Assert(LocalChatBridge.ToDeltas(null).Count == 1, "a null body ends the turn instead of crashing");
        return Task.CompletedTask;
    }

    private static Task PrepareBodyAsync()
    {
        var prepared = ConexyAI.Desktop.Tools.LocalModelClient.PrepareBody(
            """{"model":"deepseek-flash","stream":true,"stream_options":{"include_usage":true},"thinking":{"type":"enabled"},"messages":[{"role":"user","content":"hi"}]}""",
            "qwen2.5-coder:7b");
        using var document = JsonDocument.Parse(prepared);
        var root = document.RootElement;
        Assert(root.GetProperty("stream").ValueKind == JsonValueKind.False, "the local request is not streamed");
        Assert(!root.TryGetProperty("stream_options", out _), "stream_options is removed");
        Assert(!root.TryGetProperty("thinking", out _), "thinking config is removed");
        Assert(root.GetProperty("model").GetString() == "qwen2.5-coder:7b", "the configured local model replaces the cloud one");
        Assert(root.GetProperty("messages").GetArrayLength() == 1, "messages are preserved");

        var broken = ConexyAI.Desktop.Tools.LocalModelClient.PrepareBody("not json", "m");
        Assert(broken == "not json", "broken bodies are passed through unchanged");
        return Task.CompletedTask;
    }
}

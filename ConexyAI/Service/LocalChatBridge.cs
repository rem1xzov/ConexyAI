using System.Text.Json;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// LOCAL_WORKER: добавлено 2026-11-19 — перевод не-стримового ответа локальной модели в дельты, которые
// ждёт агентский цикл. Локальная модель (Ollama/LM Studio/llama.cpp) отвечает обычным chat.completion
// JSON; он приходит с ПК пользователя через device-канал, когда сервер до неё не достаёт.
public static class LocalChatBridge
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Разбирает тело chat.completion в дельты: текст, вызовы инструментов, usage.</summary>
    public static IReadOnlyList<StreamDelta> ToDeltas(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [new StreamDelta(FinishReason: "stop")];

        LlmChatResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<LlmChatResponse>(body, Options);
        }
        catch (JsonException)
        {
            // Не JSON — но это всё равно ответ модели, терять его нельзя.
            return [new StreamDelta(Content: body, FinishReason: "stop")];
        }

        var choice = response?.Choices?.FirstOrDefault();
        var message = choice?.Message;
        if (message is null)
            return [new StreamDelta(FinishReason: choice?.FinishReason ?? "stop")];

        var deltas = new List<StreamDelta>();
        if (message.Text is { Length: > 0 } text)
            deltas.Add(new StreamDelta(Content: text));
        if (message.ReasoningContent is { Length: > 0 } reasoning)
            deltas.Add(new StreamDelta(Reasoning: reasoning));
        if (message.ToolCalls is { Count: > 0 } toolCalls)
            deltas.Add(new StreamDelta(ToolCalls: toolCalls));

        var usage = response!.Usage;
        deltas.Add(new StreamDelta(
            FinishReason: choice!.FinishReason ?? "stop",
            TotalTokens: usage?.TotalTokens,
            Usage: ToTokenUsage(usage)));
        return deltas;
    }

    private static LlmTokenUsage? ToTokenUsage(LlmUsage? usage)
    {
        if (usage is null)
            return null;

        // OpenAI-формат кэша (prompt_tokens_details.cached_tokens) важнее поля DeepSeek.
        var cached = usage.PromptTokensDetails?.CachedTokens ?? usage.PromptCacheHitTokens;
        var uncached = usage.PromptTokens > 0
            ? Math.Max(0, usage.PromptTokens - cached)
            : usage.PromptCacheMissTokens;
        return new LlmTokenUsage(cached, uncached, usage.CompletionTokens);
    }
}

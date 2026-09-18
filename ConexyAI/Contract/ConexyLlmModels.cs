using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

/// <summary>
/// A chat message whose <c>content</c> can be either a plain string or an array of
/// OpenAI-style content blocks (text + image_url), enabling multimodal requests.
/// </summary>
public record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object? Content,
    [property: JsonPropertyName("tool_calls")] List<LlmToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null
)
{
    /// <summary>
    /// Extracts the plain-text answer from <see cref="Content"/>. The non-streaming
    /// chat.completion response deserializes <c>content</c> into <see cref="Content"/> as a
    /// <see cref="JsonElement"/> (because the property type is <c>object</c>), so a bare
    /// <c>Content as string</c> cast returns null and silently drops the answer.
    /// </summary>
    [JsonIgnore]
    public string? Text => Content switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
        _ => null
    };
}

public record LlmToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] LlmFunctionCall Function
);

public record LlmFunctionCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments
);

/// <summary>OpenAI-format content block: text or image_url.</summary>
public record ContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("image_url")] ImageUrlContent? ImageUrl = null
);

public record ImageUrlContent(
    [property: JsonPropertyName("url")] string Url
);

public static class ChatMessageFactory
{
    /// <summary>
    /// Builds the first user message. When image attachments are present they are
    /// packed as image_url content blocks alongside the text.
    /// </summary>
    public static ChatMessage User(string prompt, IReadOnlyList<TaskAttachment>? attachments = null)
    {
        var images = attachments?
            .Where(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (images is null || images.Count == 0)
        {
            return new ChatMessage("user", prompt);
        }

        var blocks = new List<ContentBlock> { new("text", prompt) };
        foreach (var image in images)
        {
            var dataUrl = $"data:{image.ContentType};base64,{image.ContentBase64}";
            blocks.Add(new ContentBlock("image_url", ImageUrl: new ImageUrlContent(dataUrl)));
        }

        return new ChatMessage("user", blocks);
    }
}

public record LlmChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
    [property: JsonPropertyName("tools")] List<object>? Tools = null,
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null,
    [property: JsonPropertyName("temperature")] double Temperature = 0.2,
    [property: JsonPropertyName("stream")] bool? Stream = null,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null,
    [property: JsonPropertyName("thinking")] ThinkingConfig? Thinking = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null
);

/// <summary>
/// Explicit on/off switch for a model that supports reasoning. <c>reasoning_effort</c> only
/// controls the depth once thinking is enabled; this field controls whether it reasons at all.
/// </summary>
public record ThinkingConfig(
    [property: JsonPropertyName("type")] string Type
);

public record LlmChatResponse(
    [property: JsonPropertyName("choices")] List<LlmChoice> Choices,
    [property: JsonPropertyName("usage")] LlmUsage? Usage = null
);

public record LlmChoice(
    [property: JsonPropertyName("message")] ChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason
);

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
public record LlmUsage(
    [property: JsonPropertyName("total_tokens")] int TotalTokens
);

/// <summary>Result of a non-streaming chat completion: the message plus token usage.</summary>
public record LlmChatResult(ChatMessage Message, int TotalTokens);

/// <summary>A single streamed delta: answer content and/or reasoning content, plus the optional finish reason and any completed tool calls.</summary>
public record StreamDelta(
    string? Content = null,
    string? Reasoning = null,
    string? FinishReason = null,
    List<LlmToolCall>? ToolCalls = null);

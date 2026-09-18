using ConexyAI.Contract;
using ConexyAI.Model;

namespace ConexyAI.Service;

public interface IConexyLlmClient
{
    Task<LlmChatResult> SendChatAsync(
        ConexyModelType modelType,
        List<ChatMessage> messages,
        List<object> tools,
        string? reasoningEffort = null,
        Guid? taskId = null,
        CancellationToken ct = default);

    IAsyncEnumerable<StreamDelta> StreamChatAsync(
        List<ChatMessage> messages,
        ConexyModelType modelType,
        string? reasoningEffort = null,
        List<object>? tools = null,
        string? toolChoice = null,
        Guid? taskId = null,
        CancellationToken ct = default);
}

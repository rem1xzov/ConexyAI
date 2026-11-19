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
        CancellationToken ct = default,
        // LOCAL_WORKER: явный маршрут (например, локальная модель для субагента); null — облачный.
        LlmRoute? route = null);

    IAsyncEnumerable<StreamDelta> StreamChatAsync(
        List<ChatMessage> messages,
        ConexyModelType modelType,
        string? reasoningEffort = null,
        List<object>? tools = null,
        string? toolChoice = null,
        Guid? taskId = null,
        CancellationToken ct = default,
        // LOCAL_WORKER: явный маршрут (например, локальная модель для субагента); null — облачный.
        LlmRoute? route = null);
}

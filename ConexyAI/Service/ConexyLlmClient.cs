using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Hub;
using ConexyAI.Model;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

public class ConexyLlmClient : IConexyLlmClient
{
    private readonly HttpClient _httpClient;
    private readonly AiUpstreamOptions _options;
    private readonly ILogger<ConexyLlmClient> _logger;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(180);
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };

    // Upper bound on output tokens so a long reasoning run cannot starve the final answer.
    private const int MaxTokens = 8192;

    public ConexyLlmClient(
        HttpClient httpClient,
        ILogger<ConexyLlmClient> logger,
        IOptions<AiUpstreamOptions> options,
        IHubContext<ConexyHub> hubContext)
    {
        _httpClient = httpClient;
        // Hard cap so a stalled upstream can never hold a request open indefinitely.
        _httpClient.Timeout = RequestTimeout;
        _logger = logger;
        _options = options.Value;
        _hubContext = hubContext;
    }

    public async Task<LlmChatResult> SendChatAsync(
        ConexyModelType modelType,
        List<ChatMessage> messages,
        List<object> tools,
        string? reasoningEffort = null,
        Guid? taskId = null,
        CancellationToken ct = default)
    {
        var (baseUrl, apiKey, upstreamModel) = ResolveUpstream(modelType);
        var resolvedReasoningEffort = ResolveReasoningEffortForModel(modelType, reasoningEffort);

        var payload = new LlmChatRequest(
            Model: upstreamModel,
            Messages: messages,
            Tools: tools.Count > 0 ? tools : null,
            ToolChoice: tools.Count > 0 ? "auto" : null,
            ReasoningEffort: resolvedReasoningEffort,
            Thinking: ResolveThinking(modelType, resolvedReasoningEffort),
            MaxTokens: MaxTokens
        );

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        _logger.LogInformation("DeepSeek request [task {TaskId}]: {Body}", taskId, json);

        try
        {
            using var response = await SendWithRetryAsync(
                () => BuildRequest(baseUrl, apiKey, json, acceptSse: false),
                HttpCompletionOption.ResponseContentRead,
                taskId,
                ct);

            _logger.LogInformation("DeepSeek response status [task {TaskId}]: {Status}", taskId, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("DeepSeek API Error [{StatusCode}]: {Body}", response.StatusCode, responseBody);
                throw new HttpRequestException($"Upstream LLM error ({response.StatusCode}): {responseBody}");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("DeepSeek response body [task {TaskId}]: {Body}", taskId, body);
            var result = JsonSerializer.Deserialize<LlmChatResponse>(body, _jsonOptions);
            var message = result?.Choices.FirstOrDefault()?.Message
                          ?? throw new InvalidOperationException("Invalid empty response from LLM upstream.");
            return new LlmChatResult(message, result?.Usage?.TotalTokens ?? 0);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "DeepSeek API request timed out after {Timeout}s", RequestTimeout.TotalSeconds);
            throw new HttpRequestException($"Upstream LLM request timed out after {RequestTimeout.TotalSeconds} seconds.", ex);
        }
    }

    private (string BaseUrl, string ApiKey, string UpstreamModel) ResolveUpstream(ConexyModelType modelType)
    {
        if (string.IsNullOrWhiteSpace(_options.DeepSeek.ApiKey))
        {
            throw new InvalidOperationException("DeepSeek API key is not configured in appsettings.json");
        }

        return modelType switch
        {
            ConexyModelType.ConexyV1Flash => (_options.DeepSeek.BaseUrl, _options.DeepSeek.ApiKey, _options.DeepSeek.FlashModel),
            ConexyModelType.ConexyV1Pro => (_options.DeepSeek.BaseUrl, _options.DeepSeek.ApiKey, _options.DeepSeek.ProAgentModel),
            ConexyModelType.ConexyCoder => (_options.DeepSeek.BaseUrl, _options.DeepSeek.ApiKey, _options.DeepSeek.FlashModel),
            _ => throw new ArgumentOutOfRangeException(nameof(modelType), modelType, "Unsupported model type.")
        };
    }

    private string? ResolveReasoningEffortForModel(ConexyModelType modelType, string? requested)
    {
        return modelType switch
        {
            // Flash never reasons: no reasoning_effort is forwarded to the upstream.
            ConexyModelType.ConexyV1Flash => null,
            // Pro reasons only when the Thinking toggle is on (requested == "high").
            ConexyModelType.ConexyV1Pro => NormalizeReasoning(requested),
            // The coder agent runs on the flash model and reasons through its tool loop.
            ConexyModelType.ConexyCoder => null,
            _ => NormalizeReasoning(requested)
        };
    }

    private static string? NormalizeReasoning(string? requested)
    {
        var value = requested?.Trim().ToLowerInvariant();
        return value is "low" or "high" or "max"
            ? value
            : null;
    }

    /// <summary>
    /// Explicit on/off switch for reasoning-capable models. <c>reasoning_effort</c> only
    /// controls depth once thinking is on, so we additionally send <c>thinking.type</c> to
    /// make the on/off state unambiguous (deepseek-flash/deepseek-v4-pro are one model with
    /// two modes). Flash and the coder (which runs on flash) never reason; Pro reasons only
    /// when a reasoning_effort resolved to a non-null value.
    /// </summary>
    private static ThinkingConfig ResolveThinking(ConexyModelType modelType, string? resolvedReasoningEffort)
    {
        var enabled = modelType == ConexyModelType.ConexyV1Pro && resolvedReasoningEffort is not null;
        return new ThinkingConfig(enabled ? "enabled" : "disabled");
    }

    public async IAsyncEnumerable<StreamDelta> StreamChatAsync(
        List<ChatMessage> messages,
        ConexyModelType modelType,
        string? reasoningEffort = null,
        List<object>? tools = null,
        string? toolChoice = null,
        Guid? taskId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (baseUrl, apiKey, upstreamModel) = ResolveUpstream(modelType);
        var resolvedReasoningEffort = ResolveReasoningEffortForModel(modelType, reasoningEffort);

        var payload = new LlmChatRequest(
            Model: upstreamModel,
            Messages: messages,
            Tools: tools is { Count: > 0 } ? tools : null,
            ToolChoice: tools is { Count: > 0 } ? toolChoice ?? "auto" : null,
            Stream: true,
            ReasoningEffort: resolvedReasoningEffort,
            Thinking: ResolveThinking(modelType, resolvedReasoningEffort),
            MaxTokens: MaxTokens
        );

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        _logger.LogInformation("DeepSeek stream request [task {TaskId}]: {Body}", taskId, json);

        // ResponseHeadersRead makes the stream available as soon as headers arrive —
        // tokens are pushed to SignalR incrementally instead of buffering the whole body.
        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(
                () => BuildRequest(baseUrl, apiKey, json, acceptSse: true),
                HttpCompletionOption.ResponseHeadersRead,
                taskId,
                ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "DeepSeek stream request timed out after {Timeout}s", RequestTimeout.TotalSeconds);
            throw new HttpRequestException($"Upstream LLM stream timed out after {RequestTimeout.TotalSeconds} seconds.", ex);
        }

        using (response)
        {
            _logger.LogInformation("DeepSeek stream status [task {TaskId}]: {Status}", taskId, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("DeepSeek API Error [{StatusCode}]: {Body}", response.StatusCode, errBody);
                throw new HttpRequestException($"Upstream LLM error ({response.StatusCode}): {errBody}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // Accumulate streamed tool_call fragments by index so a completed tool
            // call can be yielded as one unit once the stream finishes.
            var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "DeepSeek stream read timed out after {Timeout}s", RequestTimeout.TotalSeconds);
                    throw new HttpRequestException($"Upstream LLM stream timed out after {RequestTimeout.TotalSeconds} seconds.", ex);
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                if (data.Length == 0)
                {
                    continue;
                }

                string? content = null;
                string? reasoning = null;
                string? finishReason = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        {
                            finishReason = fr.GetString();
                        }

                        if (choice.TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                            {
                                content = contentEl.GetString();
                            }
                            if (delta.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
                            {
                                reasoning = reasoningEl.GetString();
                            }
                            if (delta.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                            {
                                AccumulateToolCalls(toolCallAccumulators, toolCallsEl);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed keep-alive or partial SSE chunks.
                }

                // TEMP diagnostic: log the raw SSE chunk so content/reasoning/finish_reason
                // are all observable in the backend logs.
                _logger.LogInformation("DeepSeek stream chunk [task {TaskId}]: {RawChunk}", taskId, data);
                _logger.LogInformation("DeepSeek stream parsed [task {TaskId}]: contentLen={ContentLen} reasoningLen={ReasoningLen}", taskId, content?.Length ?? 0, reasoning?.Length ?? 0);

                if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoning))
                {
                    yield return new StreamDelta(content, reasoning);
                }

                if (finishReason is not null)
                {
                    _logger.LogInformation("DeepSeek stream finished [task {TaskId}]: finish_reason={FinishReason}", taskId, finishReason);
                    if (finishReason == "length")
                    {
                        _logger.LogWarning("DeepSeek stream truncated by token limit (finish_reason=length) for task {TaskId}", taskId);
                    }
                    yield return new StreamDelta(FinishReason: finishReason);
                }
            }

            // Emit any fully-assembled tool calls (function-calling turns) as a single delta.
            if (toolCallAccumulators.Count > 0)
            {
                var calls = toolCallAccumulators
                    .OrderBy(kv => kv.Key)
                    .Select(kv => kv.Value.ToToolCall())
                    .ToList();
                yield return new StreamDelta(ToolCalls: calls);
            }
        }
    }

    private HttpRequestMessage BuildRequest(string baseUrl, string apiKey, string json, bool acceptSse)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (acceptSse)
        {
            request.Headers.Accept.ParseAdd("text/event-stream");
        }
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// Sends a request and transparently retries transient upstream failures (429 and
    /// 5xx) with an exponential backoff of 2s, 4s and 8s.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        Guid? taskId,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, completionOption, ct);
            }
            catch
            {
                request.Dispose();
                throw;
            }

            if (!IsRetryable(response.StatusCode) || attempt >= MaxRetries)
            {
                return response;
            }

            var delay = RetryDelays[attempt];
            response.Dispose();
            request.Dispose();

            _logger.LogWarning(
                "DeepSeek API returned {Status}; retrying in {Delay}s (attempt {Attempt}/{Max})...",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            if (taskId is { } id)
            {
                await _hubContext.Clients.Group($"task_{id}").SendAsync("OnAgentStatus", new
                {
                    stage = "waiting",
                    label = $"Превышен лимит запросов к AI провайдеру, ожидание {delay.TotalSeconds:0}с..."
                }, ct);
            }

            await Task.Delay(delay, ct);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>Mutable accumulator for a single streamed tool_call, keyed by its <c>index</c>.</summary>
    private sealed class ToolCallAccumulator
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public readonly StringBuilder Arguments = new();

        public LlmToolCall ToToolCall() =>
            new(Id, "function", new LlmFunctionCall(Name, Arguments.ToString()));
    }

    private static void AccumulateToolCalls(Dictionary<int, ToolCallAccumulator> accumulators, JsonElement toolCallsEl)
    {
        foreach (var tc in toolCallsEl.EnumerateArray())
        {
            if (!tc.TryGetProperty("index", out var indexEl) || indexEl.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var index = indexEl.GetInt32();
            if (!accumulators.TryGetValue(index, out var entry))
            {
                entry = new ToolCallAccumulator();
                accumulators[index] = entry;
            }

            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idEl.GetString()))
            {
                entry.Id = idEl.GetString()!;
            }

            if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
            {
                if (fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nameEl.GetString()))
                {
                    entry.Name = nameEl.GetString()!;
                }
                if (fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                {
                    entry.Arguments.Append(argsEl.GetString());
                }
            }
        }
    }
}

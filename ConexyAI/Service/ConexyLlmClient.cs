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
    // STALL_GUARD: добавлено 2026-09-21
    // With ResponseHeadersRead, HttpClient.Timeout only covers the response *headers* — a stream
    // that goes silent afterwards would hang the task indefinitely (the "6 minutes of thinking"
    // report). These two caps bound it: no chunk for StreamIdleTimeout, or the whole stream
    // running longer than MaxStreamDuration.
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxStreamDuration = TimeSpan.FromMinutes(10);
    // FLASH_TIMEOUT: добавлено 2026-09-27
    // Flash — быстрая модель: если она не выдала НИ ОДНОГО токена (включая reasoning) за 20 секунд,
    // запрос считается зависшим. Раньше сторож смотрел только на сам факт строки ответа и keep-alive
    // комментарии сбрасывали таймер, так что «думающая» минуту Flash так и не отвечала. Теперь мы
    // считаем именно прогресс (контент/reasoning/usage), а не любую строку.
    private static readonly TimeSpan FlashNoProgressTimeout = TimeSpan.FromSeconds(20);
    private const string FlashTimeoutMessage =
        "Превышен таймаут: модель Flash не ответила за 20 секунд. Обновите страницу и попробуйте снова.";
    private const string StreamTimeoutMessage =
        "Превышен таймаут: модель не отвечает слишком долго. Обновите страницу и попробуйте снова.";

    /// <summary>Сколько модель может не выдавать прогресса, прежде чем поток обрывается.</summary>
    public static TimeSpan NoProgressTimeoutFor(ConexyModelType modelType) =>
        modelType == ConexyModelType.ConexyV1Flash ? FlashNoProgressTimeout : StreamIdleTimeout;

    /// <summary>Пользовательское сообщение об обрыве потока по таймауту для этой модели.</summary>
    public static string TimeoutMessageFor(ConexyModelType modelType) =>
        modelType == ConexyModelType.ConexyV1Flash ? FlashTimeoutMessage : StreamTimeoutMessage;
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
        // PRIVACY_LOGS: изменено 2026-09-24 — ревью H3. Раньше здесь логировалось полное тело запроса:
        // инкогнито-диалоги, факты памяти, текст вложений, base64-картинки. Теперь — только метаданные.
        _logger.LogInformation("DeepSeek request [task {TaskId}]: model={Model} bodyBytes={Bytes}", taskId, payload.Model, json.Length);

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
                var responseBody = Bounded(await response.Content.ReadAsStringAsync(ct));
                _logger.LogError("DeepSeek API Error [{StatusCode}]: {Body}", response.StatusCode, responseBody);
                throw new HttpRequestException($"Upstream LLM error ({response.StatusCode}): {responseBody}");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<LlmChatResponse>(body, _jsonOptions);
            _logger.LogInformation(
                "DeepSeek response [task {TaskId}]: bodyBytes={Bytes} totalTokens={Tokens}",
                taskId, body.Length, result?.Usage?.TotalTokens ?? 0);
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
            // COWORK_MODE: both agent modes run on the same upstream model; they differ only in the
            // system prompt and the tool set (see ConexyAgentRunner.AgentProfile).
            ConexyModelType.ConexyCoder or ConexyModelType.ConexyCowork => (_options.DeepSeek.BaseUrl, _options.DeepSeek.ApiKey, _options.DeepSeek.FlashModel),
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
            ConexyModelType.ConexyCoder or ConexyModelType.ConexyCowork => null,
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
            MaxTokens: MaxTokens,
            // STREAM_USAGE: добавлено 2026-09-20 — ask for the trailing usage chunk so the
            // agent token budget is still accounted for when the turn is streamed.
            StreamOptions: new StreamOptionsConfig(true)
        );

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        // PRIVACY_LOGS: ревью H3 — только метаданные, без содержимого.
        _logger.LogInformation("DeepSeek stream request [task {TaskId}]: model={Model} bodyBytes={Bytes}", taskId, payload.Model, json.Length);

        // FLASH_TIMEOUT: добавлено 2026-09-28 — у Flash ограничен ВЕСЬ путь до первого токена,
        // включая повторы и их задержки (2+4+8с). Раньше сторож «нет прогресса» работал на каждую
        // попытку заново, поэтому при 429 от провайдера (общий ключ, несколько пользователей разом)
        // ход не отвечал заметно дольше 20 секунд, а пользователь видел растущий таймер без ответа.
        // После первого токена дедлайн снимается: длинный, но живой ответ резать нельзя.
        using var firstTokenCts = modelType == ConexyModelType.ConexyV1Flash
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        firstTokenCts?.CancelAfter(FlashNoProgressTimeout);
        var sendToken = firstTokenCts?.Token ?? ct;

        // ResponseHeadersRead makes the stream available as soon as headers arrive —
        // tokens are pushed to SignalR incrementally instead of buffering the whole body.
        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(
                () => BuildRequest(baseUrl, apiKey, json, acceptSse: true),
                HttpCompletionOption.ResponseHeadersRead,
                taskId,
                sendToken);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            if (firstTokenCts is { IsCancellationRequested: true })
            {
                _logger.LogError(
                    "Flash stream aborted: no first token within {Seconds}s on task {TaskId} (including retries).",
                    FlashNoProgressTimeout.TotalSeconds, taskId);
                throw new HttpRequestException(FlashTimeoutMessage, ex);
            }

            _logger.LogError(ex, "DeepSeek stream request timed out after {Timeout}s", RequestTimeout.TotalSeconds);
            throw new HttpRequestException($"Upstream LLM stream timed out after {RequestTimeout.TotalSeconds} seconds.", ex);
        }

        using (response)
        {
            _logger.LogInformation("DeepSeek stream status [task {TaskId}]: {Status}", taskId, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = Bounded(await response.Content.ReadAsStringAsync(ct));
                _logger.LogError("DeepSeek API Error [{StatusCode}]: {Body}", response.StatusCode, errBody);
                throw new HttpRequestException($"Upstream LLM error ({response.StatusCode}): {errBody}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // STALL_GUARD: linked token so an idle stream or an over-long stream aborts with a
            // readable error instead of leaving the user watching a spinner.
            // FLASH_TIMEOUT: сюда же подключён дедлайн до первого токена — пока он не снят, поток
            // оборвётся по нему; после первого токена живёт только сторож простоя.
            using var streamCts = firstTokenCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : CancellationTokenSource.CreateLinkedTokenSource(ct, firstTokenCts.Token);
            var streamDeadline = DateTime.UtcNow + MaxStreamDuration;

            // FLASH_TIMEOUT: у Flash свой, более короткий порог «нет прогресса». Считаем время с
            // последнего meaningful-токена, а не с последней строки потока.
            var progressTimeout = NoProgressTimeoutFor(modelType);
            var timeoutMessage = TimeoutMessageFor(modelType);
            var lastProgressAt = DateTime.UtcNow;

            // Accumulate streamed tool_call fragments by index so a completed tool
            // call can be yielded as one unit once the stream finishes.
            var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();

            while (true)
            {
                if (DateTime.UtcNow > streamDeadline)
                {
                    _logger.LogError(
                        "DeepSeek stream exceeded {Minutes} minutes on task {TaskId}; aborting.",
                        MaxStreamDuration.TotalMinutes, taskId);
                    throw new HttpRequestException(
                        $"Upstream LLM stream ran longer than {MaxStreamDuration.TotalMinutes:0} minutes.");
                }

                // FLASH_TIMEOUT: нет токенов дольше порога — модель зависла (либо шлёт только
                // keep-alive). Обрываем поток понятной пользователю ошибкой.
                if (DateTime.UtcNow - lastProgressAt > progressTimeout)
                {
                    _logger.LogError(
                        "DeepSeek stream made no progress for {Seconds}s on task {TaskId}; aborting.",
                        progressTimeout.TotalSeconds, taskId);
                    throw new HttpRequestException(timeoutMessage);
                }

                string? line;
                try
                {
                    // Reset the idle timer for every chunk, including keep-alive comments.
                    streamCts.CancelAfter(progressTimeout);
                    line = await reader.ReadLineAsync(streamCts.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(
                        ex,
                        "DeepSeek stream stalled: no data for {IdleSeconds}s on task {TaskId}.",
                        progressTimeout.TotalSeconds, taskId);
                    throw new HttpRequestException(timeoutMessage, ex);
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
                int? totalTokens = null;
                // FLASH_TIMEOUT: признак того, что чанк был не пустышкой (keep-alive его не ставит).
                var sawProgress = false;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    // STREAM_USAGE: the usage chunk arrives last and carries no choices.
                    if (doc.RootElement.TryGetProperty("usage", out var usageEl) &&
                        usageEl.ValueKind == JsonValueKind.Object &&
                        usageEl.TryGetProperty("total_tokens", out var totalTokensEl) &&
                        totalTokensEl.ValueKind == JsonValueKind.Number)
                    {
                        totalTokens = totalTokensEl.GetInt32();
                        sawProgress = true;
                    }

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
                                sawProgress = true;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed keep-alive or partial SSE chunks.
                }

                // FLASH_TIMEOUT: прогресс — это контент/reasoning/usage/tool_calls. Пустая строка или
                // keep-alive комментарий таймер НЕ сбрасывает, именно поэтому «молчащая» модель
                // теперь обрывается по таймауту, а не висит бесконечно.
                if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoning))
                {
                    sawProgress = true;
                    // FLASH_TIMEOUT: первый токен пришёл — снимаем дедлайн до первого токена,
                    // чтобы длинный (но живой) ответ не оборвался на 20-й секунде.
                    firstTokenCts?.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                if (sawProgress)
                {
                    lastProgressAt = DateTime.UtcNow;
                }

                // PRIVACY_LOGS: ревью H3 — сырые SSE-чанки (текст ответа) больше не логируются: строка на
                // каждый чанк раскрывала содержимое, включая инкогнито, и забивала диск.

                if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoning))
                {
                    yield return new StreamDelta(content, reasoning);
                }

                if (totalTokens is not null)
                {
                    yield return new StreamDelta(TotalTokens: totalTokens);
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

            if (!IsRetryable(response.StatusCode))
            {
                return response;
            }

            var retryAfter = RetryAfterOf(response);
            var status = response.StatusCode;

            if (attempt >= MaxRetries)
            {
                // UPSTREAM_OVERLOAD: все повторы исчерпаны, а провайдер всё ещё перегружен. Отдаём
                // пользователю понятную фразу вместо тела ответа провайдера — и обязательно
                // избавляемся от response, иначе соединение останется висеть.
                response.Dispose();
                request.Dispose();

                _logger.LogError(
                    "DeepSeek still unavailable after {Attempts} attempts on task {TaskId}: {Status}.",
                    MaxRetries + 1, taskId, (int)status);

                throw new UpstreamBusyException(BusyMessage(status, retryAfter));
            }

            // Уважаем Retry-After, но не растягиваем ход: длинную паузу провайдера обещаем
            // пользователю в тексте ошибки, а сами ждём не больше нашего бэкоффа.
            var delay = retryAfter is { } suggested && suggested <= TimeSpan.FromSeconds(10)
                ? suggested
                : RetryDelays[attempt];

            response.Dispose();
            request.Dispose();

            _logger.LogWarning(
                "DeepSeek API returned {Status}; retrying in {Delay}s (attempt {Attempt}/{Max})...",
                (int)status, delay.TotalSeconds, attempt + 1, MaxRetries);

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

    /// <summary>Подсказка провайдера, сколько подождать (секунды или абсолютная дата).</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta > TimeSpan.Zero ? delta : null;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    /// <summary>
    /// UPSTREAM_OVERLOAD: фраза для пользователя. Без тела ответа провайдера и без технических
    /// деталей — только причина и примерное время ожидания.
    /// </summary>
    public static string BusyMessage(HttpStatusCode status, TimeSpan? retryAfter)
    {
        var seconds = Math.Max(30, (int)Math.Ceiling((retryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds));
        var wait = seconds >= 60 ? $"{Math.Max(1, seconds / 60)} мин." : $"{seconds} сек.";

        return status == HttpStatusCode.TooManyRequests
            ? $"Сейчас высокая нагрузка на ИИ. Подождите примерно {wait} и отправьте сообщение снова."
            : $"Сервис ИИ временно недоступен. Попробуйте снова через {wait}.";
    }

    // PRIVACY_LOGS: тело ошибки upstream может цитировать запрос — в лог и клиенту идёт только начало.
    private static string Bounded(string text) => text.Length <= 500 ? text : text[..500] + "…";

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

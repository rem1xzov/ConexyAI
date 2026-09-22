using System.Text;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Hub;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

public class ConexyBackgroundWorker : BackgroundService
{
    private readonly IConexyQueue _queue;
    private readonly IConexyQueueGuard _queueGuard;
    private readonly IConexyCancellationRegistry _cancellations;
    private readonly IConexyTodoService _todoService;
    // COMMAND_CONFIRM: добавлено 2026-09-20
    private readonly IPendingActionService _pendingActions;
    // INCOGNITO_CHAT: добавлено 2026-09-20
    private readonly IIncognitoChatStore _incognitoChat;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly ILogger<ConexyBackgroundWorker> _logger;
    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    private readonly IOptions<MemoryOptions> _memoryOptions;

    public ConexyBackgroundWorker(
        IConexyQueue queue,
        IConexyQueueGuard queueGuard,
        IConexyCancellationRegistry cancellations,
        IConexyTodoService todoService,
        IPendingActionService pendingActions,
        IIncognitoChatStore incognitoChat,
        IServiceScopeFactory scopeFactory,
        IHubContext<ConexyHub> hubContext,
        IConexyWorkspaceService workspaceService,
        IOptions<MemoryOptions> memoryOptions,
        ILogger<ConexyBackgroundWorker> logger)
    {
        _queue = queue;
        _queueGuard = queueGuard;
        _cancellations = cancellations;
        _todoService = todoService;
        _pendingActions = pendingActions;
        _incognitoChat = incognitoChat;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _workspaceService = workspaceService;
        _memoryOptions = memoryOptions;
        _logger = logger;
    }

    private const string StudentsSystemPrompt =
        """
        Ты — ConexyAI, терпеливый Сократический ментор в обучающем режиме.
        Никогда не выдавай готовый код или полное решение сразу. Задавай наводящие технические вопросы, разбивай сложную задачу на шаги, обучай архитектурным паттернам и прямо указывай на ошибки в логике, подводя ученика к самостоятельному решению.
        """;

    // Base assistant prompt for the Chat tab (used by both ConexyV1-flash and
    // ConexyV1-pro). The chat models have no tool access and must hand off
    // autonomous work to the Agent tab (conexy-coder).
    private const string ChatSystemPrompt =
        """
        Ты — ConexyAI, дружелюбный и интеллектуальный AI-ассистент.
        Твоя роль: ответы на вопросы, генерация и объяснение базового кода, аналитика, перевод и повседневные диалоги.
        ВАЖНОЕ ОГРАНИЧЕНИЕ: У тебя НЕТ прямого доступа к файловой системе, терминалу, запуску тестов и GitHub.
        Если пользователь просит автономно написать проект, выполнить команды в консоли, внести изменения в файлы репозитория или сделать скриншот интерфейса — сухо и прямо направь его во вкладку «Agent» (модель conexy-coder), которая спроектирована специально для автономной разработки.
        Форматируй ответы с помощью Markdown: **жирный** для ключевых фактов/цифр/названий, заголовки `##` для структурирования длинных ответов, списки (`-` или `1.`) для перечислений.
        Команды для выполнения в терминале ВСЕГДА оформляй как код-блок (тройные бэктики с указанием языка, например ```powershell или ```bash), а не как инлайн-код (одинарные бэктики) внутри предложения — даже если это одна короткая команда.
        """;

    // Added only when the Smart Search toggle is on (i.e. web_search is actually in the
    // tool list for this request). Chat models were too cautious and would answer "I can't
    // verify that" instead of using the available tool, so we make the expectation explicit.
    private const string SmartSearchSystemPrompt =
        """
        Тебе доступен инструмент `web_search`. Пользователь включил «Умный поиск» — это означает, что он ожидает от тебя использования актуальной информации из интернета.
        Используй `web_search`, когда вопрос касается: текущей даты/времени, недавних событий, актуальных версий/цен/статусов, любых фактов, которые могут быть неизвестны из твоих обучающих данных или могли измениться.
        Не отказывайся от использования доступного инструмента из излишней осторожности — если сомневаешься, нужен ли поиск, лучше поищи и дай точный ответ, чем скажи, что не можешь ответить.
        """;

    // Conservative cap for how many prior chat messages are sent to DeepSeek in a single
    // request, keeping well inside the context window. The full history stays in the DB;
    // only the oldest messages are dropped from the prompt when the chat grows long.
    private const int MaxHistoryMessages = 20;
    private const int MaxSearchRounds = 3;

    // CONTINUE_GENERATION: добавлено 2026-09-21
    // Last system message when the user resumes a stopped answer. The partial text is already in
    // the context as the model's own assistant turn, so this only has to say "keep going".
    private const string ContinueInstruction =
        """
        Пользователь остановил твой предыдущий ответ и просит продолжить.
        Продолжи ровно с того места, где текст оборвался: не повторяй написанное, не начинай заново, не добавляй пояснений о том, что ты продолжаешь — просто допиши ответ до конца.
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(ConexyJob job, CancellationToken workerToken)
    {
        // Link the worker's lifetime token with a per-task token so a user-initiated stop
        // cancels just this generation while leaving the queue worker alive.
        var taskToken = _cancellations.Acquire(job.TaskId, workerToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();
            var chatHistory = scope.ServiceProvider.GetRequiredService<IChatHistoryRepository>();
            var runner = scope.ServiceProvider.GetRequiredService<IConexyAgentRunner>();
            var llmClient = scope.ServiceProvider.GetRequiredService<IConexyLlmClient>();
            var webSearch = scope.ServiceProvider.GetRequiredService<IWebSearchService>();
            // SUBSCRIPTION_TIERS: добавлено 2026-09-17
            var subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            var memoryService = scope.ServiceProvider.GetRequiredService<IUserMemoryService>();

            var entity = await repository.GetByIdAsync(job.TaskId, taskToken);
            if (entity == null) return;

            entity.Status = ConexyStatus.Running;
            await repository.SaveChangesAsync(taskToken);

            try
            {
                // Materialize non-graphical attachments into the sandbox before the loop.
                // ATTACHMENT_ERRORS: failures are reported instead of being swallowed.
                var failedAttachments = await _workspaceService.SaveAttachmentsAsync(job.ChatId, job.Attachments, taskToken);
                if (failedAttachments.Count > 0)
                {
                    await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync(
                        "OnLog",
                        $"[Attachments] Не удалось сохранить в рабочую область: {string.Join(", ", failedAttachments)}. Файлы не будут доступны агенту.",
                        taskToken);
                }

                string result;
                // conexy-coder -> autonomous agent pipeline (Maker-Checker + tools).
                // Flash/Pro -> streaming dialog (Pro is the reasoning chat model and
                // also powers the Socratic Students mode).
                if (job.ModelType == ConexyModelType.ConexyCoder)
                {
                    result = await runner.RunLoopAsync(job, taskToken);

                    // AGENT_HISTORY: добавлено 2026-09-22 — раньше агентский путь НИЧЕГО не писал
                    // в историю чата (запись жила только внутри StreamCompletionAsync). Из-за этого
                    // на второе сообщение в том же чате агент отвечал «это первое сообщение,
                    // контекста нет», а его собственные ответы не сохранялись вообще.
                    // CONTINUE_GENERATION: resumed turn stores prefix + continuation.
                    var storedAgentResult = string.IsNullOrWhiteSpace(job.AssistantPrefix)
                        ? result
                        : job.AssistantPrefix + result;
                    await PersistTurnAsync(chatHistory, memoryService, job, storedAgentResult, taskToken);
                    result = storedAgentResult;
                }
                else
                {
                    result = await StreamCompletionAsync(llmClient, chatHistory, webSearch, subscriptionService, memoryService, job, taskToken);
                }

                entity.Result = job.Incognito ? ConexyService.IncognitoPromptPlaceholder : result;
                entity.Status = ConexyStatus.Completed;
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(taskToken);

                await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnCompleted", new
                {
                    TaskId = job.TaskId,
                    Result = result
                }, taskToken);
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
                // Host is shutting down — nothing to persist or report to a disconnecting client.
                _logger.LogInformation("Task {TaskId} aborted due to host shutdown.", job.TaskId);
            }
            catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
            {
                // User-initiated stop. Partial content already streamed to the client stays
                // as-is; we only mark the entity stopped and notify the group.
                _logger.LogInformation("Task {TaskId} stopped by user.", job.TaskId);

                entity.Status = ConexyStatus.Cancelled;
                entity.Result ??= "Generation stopped.";
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);

                // WORKSPACE_PERSISTENCE: удалено 2026-09-22 — здесь удалялся весь воркспейс
                // разговора (CleanupWorkspaceAsync → Directory.Delete recursive). После остановки
                // или ошибки агент на следующее сообщение видел пустую папку и справедливо
                // отвечал, что файлов нет. Воркспейс живёт ровно столько, сколько живёт чат.
                await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnStopped", job.TaskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute task {TaskId}", job.TaskId);

                entity.Status = ConexyStatus.Failed;
                entity.Result = ex.Message;
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);

                // WORKSPACE_PERSISTENCE: см. комментарий в ветке OnStopped — файлы сессии больше
                // не удаляются при ошибке/остановке агента.
                await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnError", ex.Message);
            }
        }
        catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
        {
            // Cancellation arrived before the run path was entered (e.g. during setup).
        }
        catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
        {
            // Stop requested before processing started — tell the client so its UI finalizes.
            _logger.LogInformation("Task {TaskId} stopped before processing started.", job.TaskId);
            try { await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnStopped", job.TaskId); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process task {TaskId}", job.TaskId);
            try { await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnError", ex.Message); } catch { }
        }
        finally
        {
            _cancellations.Release(job.TaskId);
            // Release the dedup guard once the task is fully processed (success, failure
            // or skipped) so a later request for the same session can enqueue again.
            _queueGuard.MarkCompleted(job.TaskId);
            // Drop the transient todo state so it does not accumulate on the long-lived worker.
            _todoService.Clear(job.TaskId);
            // COMMAND_CONFIRM: добавлено 2026-09-20
            // "Allow all for this session" must not survive into the next agent task — a fresh
            // run always starts by asking for confirmation again.
            _pendingActions.SetTaskAutoApproval(job.TaskId, false);
        }
    }

    private async Task<string> StreamCompletionAsync(
        IConexyLlmClient llmClient,
        IChatHistoryRepository chatHistory,
        IWebSearchService webSearch,
        ISubscriptionService subscriptionService,
        IUserMemoryService memoryService,
        ConexyJob job,
        CancellationToken ct)
    {
        var systemPrompt = job.StudentsMode
            ? StudentsSystemPrompt
            : ChatSystemPrompt;

        // Always inject the current server time so "what day/time is it" questions are
        // answered directly and reliably, without depending on the web search provider.
        systemPrompt += $"\n\nТекущая дата и время (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}.";

        // SUBSCRIPTION_TIERS: добавлено 2026-09-17 — inject durable user memory facts.
        // INCOGNITO_CHAT: добавлено 2026-09-20 — an incognito turn must not read the user's
        // long-term memory, otherwise the profile would leak into a "forgotten" chat.
        if (!job.Incognito)
        {
            var memoryBlock = await memoryService.BuildPromptBlockAsync(job.UserId, ct);
            if (!string.IsNullOrEmpty(memoryBlock))
                systemPrompt += "\n" + memoryBlock;
        }

        // web_search is available to flash/pro only when the Smart Search toggle is on.
        var searchEnabled = job.ModelType != ConexyModelType.ConexyCoder && job.SmartSearch;

        // Rebuild the prior turns of this chat so the model has conversational context.
        // INCOGNITO_CHAT: добавлено 2026-09-20 — incognito threads live in memory only, so they
        // keep their context without ever touching the history table.
        IReadOnlyList<ConexyChatMessageEntity> history = job.Incognito
            ? _incognitoChat.GetMessages(job.ChatId)
            : await chatHistory.GetMessagesAsync(job.UserId, job.ChatId, ct);

        var messages = new List<ChatMessage> { new("system", systemPrompt) };
        if (searchEnabled)
        {
            messages.Add(new("system", SmartSearchSystemPrompt));
        }

        IReadOnlyList<ConexyChatMessageEntity> retained = history;
        if (history.Count > MaxHistoryMessages)
        {
            retained = history.Skip(history.Count - MaxHistoryMessages).ToList();
            _logger.LogWarning(
                "Trimming chat history for {ChatId}: {Total} messages, retaining last {Kept} to stay within the context window.",
                job.ChatId, history.Count, MaxHistoryMessages);
        }

        foreach (var m in retained)
        {
            messages.Add(new ChatMessage(m.Role, m.Content));
        }

        messages.Add(ChatMessageFactory.User(job.Prompt, job.Attachments));

        // CONTINUE_GENERATION: добавлено 2026-09-21 — the user stopped mid-answer, so hand the
        // partial text back as the model's own truncated turn and tell it to carry on.
        if (!string.IsNullOrWhiteSpace(job.AssistantPrefix))
        {
            messages.Add(new ChatMessage("assistant", job.AssistantPrefix));
            messages.Add(new ChatMessage("system", ContinueInstruction));
        }

        // Flash never reasons; Pro reasons only when the Thinking toggle is on
        // (or in Students mode). The LlmClient maps per model type.
        var reasoningEffort = job.ModelType == ConexyModelType.ConexyV1Pro && (job.Thinking || job.StudentsMode)
            ? "high"
            : null;

        var group = _hubContext.Clients.Group($"task_{job.TaskId}");

        // Smart Search (flash/pro only): give the model the web_search tool and run a small
        // tool-calling loop. conexy-coder already has web_search unconditionally via the runner.
        string result;
        if (searchEnabled)
        {
            result = await SearchAwareCompletionAsync(llmClient, webSearch, job, messages, reasoningEffort, group, ct);
        }
        else
        {
            var builder = new StringBuilder();
            var truncated = false;

            await foreach (var delta in llmClient.StreamChatAsync(messages, job.ModelType, reasoningEffort, taskId: job.TaskId, ct: ct))
            {
                if (!string.IsNullOrEmpty(delta.Reasoning))
                {
                    await group.SendAsync("OnThinkingToken", delta.Reasoning, ct);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    builder.Append(delta.Content);
                    await group.SendAsync("OnContentToken", delta.Content, ct);
                }

                if (delta.FinishReason == "length")
                {
                    truncated = true;
                }
            }

            // Surface a truncation instead of silently returning an empty/partial answer.
            if (truncated)
            {
                var note = "\n\n[Ответ оборван: достигнут лимит токенов модели]";
                builder.Append(note);
                await group.SendAsync("OnContentToken", note, ct);
            }

            result = builder.ToString();
        }

        // Persist the completed turn so the next message in this chat includes it.
        // CONTINUE_GENERATION: a resumed turn stores the prefix plus the continuation, otherwise
        // the history would keep only the tail of the answer.
        var storedResult = string.IsNullOrWhiteSpace(job.AssistantPrefix)
            ? result
            : job.AssistantPrefix + result;
        await PersistTurnAsync(chatHistory, memoryService, job, storedResult, ct);

        // SUBSCRIPTION_TIERS: добавлено 2026-09-17
        // INCOGNITO_CHAT: limits still apply — incognito hides history, it is not a free pass.
        await subscriptionService.RecordRequestAsync(job.UserId, job.ModelType, ct);

        return storedResult;
    }

    // AGENT_HISTORY: добавлено 2026-09-22
    /// <summary>
    /// Stores one completed turn (user prompt + final answer) so the next message in the same
    /// chat is answered with real context, and enqueues long-term memory extraction on the usual
    /// batching rule. Shared by the flash/pro path and the conexy-coder path — the coder path used
    /// to skip this entirely, which is why the agent answered the second message of a conversation
    /// as if it were the first one.
    /// </summary>
    private async Task PersistTurnAsync(
        IChatHistoryRepository chatHistory,
        IUserMemoryService memoryService,
        ConexyJob job,
        string storedResult,
        CancellationToken ct)
    {
        // INCOGNITO_CHAT: incognito turns stay in memory and never produce a ChatHistory row
        // (so the chat also never shows up in the sidebar history).
        if (job.Incognito)
        {
            _incognitoChat.Append(job.ChatId, job.UserId, "user", job.Prompt);
            if (!string.IsNullOrWhiteSpace(storedResult))
            {
                _incognitoChat.Append(job.ChatId, job.UserId, "assistant", storedResult);
            }
            return;
        }

        // User text is stored as plain text; image attachments are not part of history.
        await chatHistory.AppendAsync(job.UserId, job.ChatId, "user", job.Prompt, ct);
        if (!string.IsNullOrWhiteSpace(storedResult))
        {
            await chatHistory.AppendAsync(job.UserId, job.ChatId, "assistant", storedResult, ct);
        }

        // Memory extraction batching: run after every N-th user message in this chat.
        var userMessageCount = await chatHistory.CountUserMessagesAsync(job.UserId, job.ChatId, ct);
        if (userMessageCount > 0 && userMessageCount % _memoryOptions.Value.BatchingThreshold == 0)
        {
            memoryService.EnqueueExtraction(job.UserId, job.ChatId);
        }
    }

    private async Task<string> SearchAwareCompletionAsync(
        IConexyLlmClient llmClient,
        IWebSearchService webSearch,
        ConexyJob job,
        List<ChatMessage> messages,
        string? reasoningEffort,
        IClientProxy group,
        CancellationToken ct)
    {
        var tools = new List<object> { WebSearchTool.Schema() };

        for (var round = 0; round < MaxSearchRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            List<LlmToolCall>? toolCalls = null;
            var truncated = false;

            // Stream the round so answer tokens reach the client incrementally (and
            // reasoning stays streamed), while still capturing any tool_calls the model
            // requests. Tool-call turns produce no content — only a ToolCalls delta.
            await foreach (var delta in llmClient.StreamChatAsync(
                messages, job.ModelType, reasoningEffort, tools, "auto", job.TaskId, ct))
            {
                if (!string.IsNullOrEmpty(delta.Reasoning))
                {
                    reasoning.Append(delta.Reasoning);
                    await group.SendAsync("OnThinkingToken", delta.Reasoning, ct);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    content.Append(delta.Content);
                    await group.SendAsync("OnContentToken", delta.Content, ct);
                }

                if (delta.ToolCalls is { Count: > 0 })
                {
                    toolCalls = delta.ToolCalls;
                }

                if (delta.FinishReason == "length")
                {
                    truncated = true;
                }
            }

            _logger.LogInformation(
                "SearchAware round {Round} [task {TaskId}]: toolCalls={ToolCallCount} textLen={TextLen}",
                round, job.TaskId, toolCalls?.Count ?? 0, content.Length);

            // Record the assistant turn (content and/or tool calls) for the next round.
            messages.Add(new ChatMessage(
                Role: "assistant",
                Content: content.Length > 0 ? content.ToString() : null,
                ToolCalls: toolCalls,
                ReasoningContent: reasoning.Length > 0 ? reasoning.ToString() : null));

            if (truncated)
            {
                var note = "\n\n[Ответ оборван: достигнут лимит токенов модели]";
                content.Append(note);
                await group.SendAsync("OnContentToken", note, ct);
            }

            if (toolCalls is null || toolCalls.Count == 0)
            {
                return content.ToString();
            }

            foreach (var toolCall in toolCalls)
            {
                if (toolCall.Function.Name != WebSearchTool.Name) continue;

                var query = ParseSearchQuery(toolCall.Function.Arguments);
                _logger.LogInformation("SearchAware tool_call [task {TaskId}]: name={Name} args={Args} query={Query}", job.TaskId, toolCall.Function.Name, toolCall.Function.Arguments, query);

                await group.SendAsync("SearchStatus", new SearchStatusEvent { Status = "searching", Query = query }, ct);
                var result = await webSearch.SearchAsync(query, ct);
                await group.SendAsync("SearchStatus", new SearchStatusEvent { Status = "completed", Query = query }, ct);
                _logger.LogInformation("SearchAware search result [task {TaskId}]: success={Success} outputLen={OutputLen}", job.TaskId, result.Success, result.Output?.Length ?? 0);

                messages.Add(new ChatMessage(Role: "tool", Content: result.Output, ToolCallId: toolCall.Id));
            }
        }

        // The model kept searching without finalising; force a final answer (streamed).
        messages.Add(new ChatMessage("system", "Stop searching and answer the user with what you have."));
        var finalBuilder = new StringBuilder();
        await foreach (var delta in llmClient.StreamChatAsync(
            messages, job.ModelType, reasoningEffort, tools, "auto", job.TaskId, ct))
        {
            if (!string.IsNullOrEmpty(delta.Content))
            {
                finalBuilder.Append(delta.Content);
                await group.SendAsync("OnContentToken", delta.Content, ct);
            }
        }
        return finalBuilder.ToString();
    }

    private static string ParseSearchQuery(string argumentsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.String)
                return q.GetString() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            // Best-effort; the search service rejects an empty query downstream.
        }
        return string.Empty;
    }
}
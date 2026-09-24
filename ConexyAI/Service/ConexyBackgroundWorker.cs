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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly ILogger<ConexyBackgroundWorker> _logger;
    // WORKER_CONCURRENCY: добавлено 2026-09-24 — ревью M13: сколько ходов обрабатывается одновременно.
    private readonly int _maxConcurrency;

    // PARTIAL_TURN_PERSIST: добавлено 2026-09-22 — текст, уже отправленный клиенту в этом ходе.
    // WORKER_CONCURRENCY: изменено 2026-09-24 — буфер теперь свой у каждого хода: ходы идут
    // параллельно, и поле синглтона перемешивало бы частичные ответы разных задач.
    private sealed class TurnBuffer
    {
        public string PartialChatText = string.Empty;
    }

    // CONVERSATION_SERVICE: добавлено 2026-09-23 — зависимости IIncognitoChatStore и
    // IOptions<MemoryOptions> убраны отсюда: и история, и батчинг памяти теперь внутри
    // ConversationService, и держать их здесь означало бы давать воркеру повод снова заняться
    // историей самостоятельно.
    public ConexyBackgroundWorker(
        IConexyQueue queue,
        IConexyQueueGuard queueGuard,
        IConexyCancellationRegistry cancellations,
        IConexyTodoService todoService,
        IPendingActionService pendingActions,
        IServiceScopeFactory scopeFactory,
        IHubContext<ConexyHub> hubContext,
        IConexyWorkspaceService workspaceService,
        ILogger<ConexyBackgroundWorker> logger,
        IConfiguration? configuration = null)
    {
        var configured = configuration?.GetValue<int?>("Worker:MaxConcurrency") ?? 0;
        _maxConcurrency = configured > 0 ? configured : DefaultMaxConcurrency;
        _queue = queue;
        _queueGuard = queueGuard;
        _cancellations = cancellations;
        _todoService = todoService;
        _pendingActions = pendingActions;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _workspaceService = workspaceService;
        _logger = logger;
    }

    private const string StudentsSystemPrompt =
        """
        Ты — ConexyAI, терпеливый Сократический ментор в обучающем режиме.
        Никогда не выдавай готовый код или полное решение сразу. Задавай наводящие технические вопросы, разбивай сложную задачу на шаги, обучай архитектурным паттернам и прямо указывай на ошибки в логике, подводя ученика к самостоятельному решению.
        Оформление ответа — как в остальных режимах: **жирный** для ключевых терминов и цифр, инлайн-код в одинарных бэктиках для имён функций/типов/команд, заголовки `##` для длинных объяснений, маркированные и нумерованные списки для шагов.
        Любое сравнение или структурированные данные (варианты решения, плюсы и минусы, сложность алгоритмов, значения на разных входах) оформляй стандартной Markdown-таблицей (GFM): строка заголовков, сразу под ней строка-разделитель `|---|---|`, затем по строке на каждую запись. Каждая строка таблицы — на своей строке текста (перенос `\n`), а НЕ всё в одну строку через пайпы.
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
        Сравнительные таблицы ВСЕГДА оформляй стандартными Markdown-таблицами (GFM): строка заголовков, сразу под ней строка-разделитель `|---|---|`, затем по строке на каждую запись. Каждая строка таблицы — на своей строке текста (перенос `\n`), а НЕ всё в одну строку через пайпы.
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
    // CONVERSATION_SERVICE: MaxHistoryMessages и ContinueInstruction убраны отсюда — теперь это
    // ConversationOptions.HistoryDepth и ConversationService.ContinueInstruction, одни на все пути.
    private const int MaxSearchRounds = 3;

    // TASK_ORPHAN_RECOVERY: добавлено 2026-09-23 — текст для задачи, которую оборвал рестарт процесса.
    internal const string InterruptedByRestartMessage =
        "Задача прервана перезапуском сервера. Отправьте запрос ещё раз.";

    // TASK_ORPHAN_RECOVERY: добавлено 2026-09-23
    //
    // Очередь живёт в памяти процесса. Если бэкенд перезапускается посреди задачи (деплой, падение,
    // OOM, рестарт контейнера), её строка навсегда остаётся Running/Pending: закрыть её больше некому,
    // сторож на клиенте честно видит `running` и опрашивает бесконечно, а «Стоп» на сервере ничего не
    // находит («no active generation»). Это ровно инцидент ec69edc4: 1006 + 502 от Cloudflare («Host:
    // Error») в 19:24:39–41, после переподключения сервер отвечает `running` без результата.
    //
    // StartAsync хостед-сервисов ожидается ДО того, как Kestrel начнёт принимать запросы, поэтому в
    // этот момент ни одна Pending/Running строка не может принадлежать живому прогону.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverOrphanedTasksAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    private async Task RecoverOrphanedTasksAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();
            var recovered = await repository.FailUnfinishedAsync(InterruptedByRestartMessage, ct);
            if (recovered.Count > 0)
            {
                _logger.LogWarning(
                    "Recovered {Count} task(s) left Pending/Running by the previous process; marked Failed: [{TaskIds}]",
                    recovered.Count, string.Join(", ", recovered));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recovery must never block startup: the worker still has to serve new tasks.
            _logger.LogError(ex, "Failed to recover tasks left unfinished by the previous process.");
        }
    }

    // WORKER_CONCURRENCY: добавлено 2026-09-24 — ревью M13. Раньше очередь читал один
    // последовательный цикл: длинный агентский прогон (до ~10 минут) блокировал flash/pro/агента
    // ВСЕХ пользователей. Теперь задачи идут параллельно с ограничением, а каждая получает свой scope,
    // свой токен отмены и свой буфер частичного ответа.
    private const int DefaultMaxConcurrency = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var running = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Task>();

        try
        {
            await foreach (var job in _queue.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                var run = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessJobAsync(job, stoppingToken);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None);
                running[job.TaskId] = run;
                _ = run.ContinueWith(_ => running.TryRemove(job.TaskId, out Task? _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: fall through and let the running turns finish their own shutdown path.
        }

        await Task.WhenAll(running.Values);
    }

    private async Task ProcessJobAsync(ConexyJob job, CancellationToken workerToken)
    {
        // Link the worker's lifetime token with a per-task token so a user-initiated stop
        // cancels just this generation while leaving the queue worker alive.
        var taskToken = _cancellations.Acquire(job.TaskId, workerToken, job.ChatId);
        var buffer = new TurnBuffer();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();
            var runner = scope.ServiceProvider.GetRequiredService<IConexyAgentRunner>();
            var llmClient = scope.ServiceProvider.GetRequiredService<IConexyLlmClient>();
            var webSearch = scope.ServiceProvider.GetRequiredService<IWebSearchService>();
            // SUBSCRIPTION_TIERS: добавлено 2026-09-17
            var subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            // CONVERSATION_SERVICE: добавлено 2026-09-23 — единая сборка контекста и запись хода.
            var conversation = scope.ServiceProvider.GetRequiredService<IConversationService>();

            // STOP_CONFIRM: добавлено 2026-09-24 — ревью M17: «Стоп» для задачи, которая ещё стояла в
            // очереди, раньше был no-op — задача потом отрабатывала целиком и тратила квоту.
            if (_cancellations.WasStoppedWhileQueued(job.TaskId))
            {
                await MarkStoppedBeforeStartAsync(job);
                return;
            }

            var entity = await repository.GetByIdAsync(job.TaskId, taskToken);
            if (entity == null) return;

            entity.Status = ConexyStatus.Running;
            await repository.SaveChangesAsync(taskToken);


            // COWORK_MODE: both agent modes go through the same runner; the runner picks the mode's
            // prompt and tools itself.
            var isAgent = job.ModelType.IsAgent();

            // CONVERSATION_SERVICE: контекст строится ОДИН раз и используется и для запроса к
            // модели, и для записи хода — поэтому они не могут разойтись по chatId, userId или
            // режиму «продолжить».
            var context = new ConversationContext(
                TaskId: job.TaskId,
                ChatId: job.ChatId,
                UserId: job.UserId,
                SystemPrompt: isAgent ? runner.GetSystemPrompt(job.ModelType) : BuildChatSystemPrompt(job),
                // OFFICE_FORMATS: document attachments are read into the message for every mode (the
                // model never saw them before), and the same text lands in the history.
                UserMessage: AttachmentText.ComposeUserMessage(job.Prompt, job.Attachments),
                Incognito: job.Incognito,
                Attachments: job.Attachments,
                AssistantPrefix: job.AssistantPrefix,
                // CHAT_KIND_SYNC: добавлено 2026-09-23 — режим чата доезжает до записи истории.
                ChatKind: job.ChatKind,
                // HISTORY_REPLAY: добавлено 2026-09-24 — ревью M5.
                Regenerate: job.Regenerate);

            var outcome = TurnOutcome.Completed;
            var result = string.Empty;

            try
            {
                // Materialize non-graphical attachments into the sandbox before the loop.
                // ATTACHMENT_ERRORS: failures are reported instead of being swallowed.
                // INCOGNITO_CHAT: изменено 2026-09-24 — ревью M7: только для агентов. У чат-моделей нет
                // инструментов, файлы на диске им не нужны (текст вложений уже в сообщении), а для
                // инкогнито-чатов эти файлы оставались на диске навсегда.
                IReadOnlyList<string> failedAttachments = isAgent
                    ? await _workspaceService.SaveAttachmentsAsync(job.ChatId, job.Attachments, taskToken)
                    : Array.Empty<string>();
                if (failedAttachments.Count > 0)
                {
                    await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync(
                        "OnLog",
                        $"[Attachments] Не удалось сохранить в рабочую область: {string.Join(", ", failedAttachments)}. Файлы не будут доступны агенту.",
                        taskToken);
                }

                if (isAgent)
                {
                    // conexy-coder / conexy-cowork -> autonomous agent pipeline (tools; Maker-Checker for code).
                    result = await runner.RunLoopAsync(job, context, taskToken);
                }
                else
                {
                    // Flash/Pro -> streaming dialog (Pro is the reasoning chat model and
                    // also powers the Socratic Students mode).
                    result = await StreamCompletionAsync(llmClient, conversation, context, webSearch, job, buffer, taskToken);

                    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
                    // INCOGNITO_CHAT: limits still apply — incognito hides history, it is not a free pass.
                    await subscriptionService.RecordRequestAsync(job.UserId, job.ModelType, taskToken);
                }

                // entity.Result mirrors exactly what the history stores, prefix included.
                // CONVERSATION_SERVICE: both the entity and the OnCompleted payload use the same
                // composed text, so a resumed turn reads identically in the DB, in the payload and
                // in the chat history.
                var storedResult = ConversationService.ComposeStoredText(job.AssistantPrefix, result);
                entity.Result = job.Incognito ? ConexyService.IncognitoPromptPlaceholder : storedResult;
                entity.Status = ConexyStatus.Completed;
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(taskToken);

                // TASK_COMPLETION_DIAGNOSTICS: добавлено 2026-09-22 — без этой строки нельзя было
                // доказать, что бэкенд реально закрыл задачу, и разбор «висит генерация» упирался
                // в догадки. Теперь видно и завершение, и факт отправки OnCompleted.
                _logger.LogInformation(
                    "Task {TaskId} finished; broadcasting OnCompleted ({Chars} char(s) of result).",
                    job.TaskId, storedResult.Length);

                await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnCompleted", new
                {
                    TaskId = job.TaskId,
                    Result = storedResult
                }, taskToken);
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
                outcome = TurnOutcome.Stopped;
                _logger.LogInformation("Task {TaskId} aborted due to host shutdown.", job.TaskId);

                // TASK_ORPHAN_RECOVERY: добавлено 2026-09-23 — раньше эта ветка только писала лог, и
                // строка задачи оставалась Running навсегда. Закрываем её здесь; жёсткое убийство
                // процесса (SIGKILL/OOM) эту ветку не пройдёт — его подбирает RecoverOrphanedTasksAsync.
                entity.Status = ConexyStatus.Failed;
                entity.Result = InterruptedByRestartMessage;
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);

                try
                {
                    await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnError", InterruptedByRestartMessage);
                }
                catch (Exception notifyEx)
                {
                    // Connections are usually already closed at this point; the client's watchdog
                    // reads the status from the task record after it reconnects.
                    _logger.LogDebug(notifyEx, "Could not notify task {TaskId} about the shutdown.", job.TaskId);
                }
            }
            catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
            {
                // User-initiated stop. Partial content already streamed to the client stays
                // as-is; we only mark the entity stopped and notify the group.
                outcome = TurnOutcome.Stopped;
                _logger.LogInformation("Task {TaskId} stopped by user.", job.TaskId);

                entity.Status = ConexyStatus.Cancelled;
                entity.Result ??= "Generation stopped.";
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);

                await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnStopped", job.TaskId);
            }
            catch (Exception ex)
            {
                outcome = TurnOutcome.Failed;
                _logger.LogError(ex, "Failed to execute task {TaskId}", job.TaskId);

                entity.Status = ConexyStatus.Failed;
                entity.Result = ex.Message;
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);

                await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnError", ex.Message);
            }
            finally
            {
                // CONVERSATION_SERVICE: сердце рефакторинга. Запись хода живёт ЗДЕСЬ, а не в каждой
                // ветке по отдельности, поэтому любой исход — завершение, остановка, ошибка —
                // сохраняет диалог, и любая будущая catch-ветка унаследует это автоматически.
                // Именно асимметрия «в одной ветке пишем, в другой забыли» дважды ломала контекст.
                try
                {
                    var assistantText = outcome == TurnOutcome.Completed ? result : PartialAssistantText(runner, buffer);
                    await conversation.PersistTurnAsync(context, assistantText, outcome, CancellationToken.None);
                }
                catch (Exception persistEx)
                {
                    // Сбой записи истории не должен подменять исходную ошибку или остановку.
                    _logger.LogError(persistEx, "Failed to persist conversation turn for task {TaskId}.", job.TaskId);
                }
            }
        }
        catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
        {
            // Cancellation arrived before the run path was entered (e.g. during setup).
        }
        catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
        {
            // Stop requested before processing started — close the row and tell the client.
            _logger.LogInformation("Task {TaskId} stopped before processing started.", job.TaskId);
            await MarkStoppedBeforeStartAsync(job);
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

    // STOP_CONFIRM: добавлено 2026-09-24 — задача остановлена до старта: строка закрывается как
    // Cancelled (раньше оставалась Pending навсегда), клиент получает OnStopped.
    private async Task MarkStoppedBeforeStartAsync(ConexyJob job)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();
            var entity = await repository.GetByIdAsync(job.TaskId, CancellationToken.None);
            if (entity is not null && entity.Status is ConexyStatus.Pending or ConexyStatus.Running)
            {
                entity.Status = ConexyStatus.Cancelled;
                entity.Result ??= "Generation stopped.";
                entity.FinishedAt = DateTime.UtcNow;
                await repository.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not close task {TaskId} stopped before it started.", job.TaskId);
        }

        try { await _hubContext.Clients.Group($"task_{job.TaskId}").SendAsync("OnStopped", job.TaskId); } catch { }
    }

    private async Task<string> StreamCompletionAsync(
        IConexyLlmClient llmClient,
        IConversationService conversation,
        ConversationContext context,
        IWebSearchService webSearch,
        ConexyJob job,
        TurnBuffer buffer,
        CancellationToken ct)
    {
        // web_search is available to flash/pro only when the Smart Search toggle is on.
        var searchEnabled = !job.ModelType.IsAgent() && job.SmartSearch;

        // CONVERSATION_SERVICE: system prompt, memory facts, prior turns, the current user message
        // and the resumed-answer prefix are all assembled by the shared service now. This method
        // no longer knows how history works — which is what keeps it from drifting again.
        var messages = await conversation.BuildRequestAsync(context, ct);
        if (searchEnabled)
        {
            messages.Insert(1, new("system", SmartSearchSystemPrompt));
        }

        // Flash never reasons. Pro reasons only when the Thinking toggle is on — in students mode too:
        // study mode now exposes the same toggle, so it must actually drive the depth instead of being
        // forced on. STUDENTS_REASONING: previously this read `job.Thinking || job.StudentsMode`, which
        // hard-coded "high" for every students request and made the toggle a no-op there.
        var reasoningEffort = job.ModelType == ConexyModelType.ConexyV1Pro && job.Thinking
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

            // PARTIAL_TURN_PERSIST: добавлено 2026-09-22 — если пользователь остановит генерацию,
            // исключение разматывает стек и локальный builder теряется. Сохраняем то, что уже
            // успело прийти, чтобы ход всё равно попал в историю чата.
            try
            {
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
            }
            catch (OperationCanceledException)
            {
                buffer.PartialChatText = builder.ToString();
                throw;
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
        // CONVERSATION_SERVICE: the history is written by the caller's finally, once, for every
        // outcome — this method only produces the text.
        return result;
    }

    // CONVERSATION_SERVICE: добавлено 2026-09-23
    //
    // Здесь раньше жили два метода записи истории: PersistTurnAsync (только успешный путь) и
    // PersistInterruptedTurnAsync (стоп/ошибка). Оба удалены — запись теперь ОДНА и находится в
    // finally вызывающего, внутри ConversationService. Именно это дублирование и породило два
    // инцидента с потерей контекста: ветки расходились, и одна из них забывала запись.

    /// <summary>
    /// System prompt for the dialog paths (chat / students). The coder path takes its prompt from
    /// the agent runner instead. The durable-memory block is appended by
    /// <see cref="IConversationService.BuildRequestAsync"/>, so it is not added here.
    /// </summary>
    private static string BuildChatSystemPrompt(ConexyJob job)
    {
        var systemPrompt = job.StudentsMode
            ? StudentsSystemPrompt
            : ChatSystemPrompt;

        // ARTIFACTS: добавлено 2026-09-24 — ТЗ 2, §1–2: интерактивные артефакты, Mermaid и LaTeX
        // рендерятся на клиенте во всех режимах чата, поэтому модель знает об этих форматах и здесь.
        systemPrompt += "\n\n" + Prompts.PromptFragments.Artifacts + "\n\n" + Prompts.PromptFragments.RichFormatting;

        // Always inject the current server time so "what day/time is it" questions are
        // answered directly and reliably, without depending on the web search provider.
        systemPrompt += $"\n\nТекущая дата и время (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}.";
        return systemPrompt;
    }

    /// <summary>
    /// The assistant text to store for a turn that did not complete: the chat path keeps its
    /// buffer in a field, the agent path exposes everything it streamed on the runner.
    /// </summary>
    private static string PartialAssistantText(IConexyAgentRunner runner, TurnBuffer buffer) =>
        string.IsNullOrWhiteSpace(buffer.PartialChatText) ? runner.PartialOutput : buffer.PartialChatText;

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
                // H3: the query is the user's text — only its length is logged.
                _logger.LogInformation("SearchAware tool_call [task {TaskId}]: name={Name} queryChars={QueryChars}", job.TaskId, toolCall.Function.Name, query.Length);

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
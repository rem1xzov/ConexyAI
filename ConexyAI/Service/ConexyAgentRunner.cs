using System.Text;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Hub;
using ConexyAI.Model;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

public class ConexyAgentRunner : IConexyAgentRunner
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly IConexyVisionService _visionService;
    private readonly IConexyLlmClient _llmClient;
    private readonly IConexyGitHubService _githubService;
    private readonly IConexyEditorService _editorService;
    private readonly IConexyBashService _bashService;
    private readonly IConexyTodoService _todoService;
    private readonly IWebSearchService _webSearchService;
    private readonly IHubContext<ConexyHub> _hubContext;
    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    private readonly IDangerousCommandClassifier _dangerousCommandClassifier;
    private readonly IPendingActionService _pendingActionService;
    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    private readonly ISubscriptionService _subscriptionService;
    private readonly IUserMemoryService _memoryService;
    // RAG: добавлено 2026-09-17
    private readonly IDocumentService _documentService;
    private ConexyJob _job = null!;

    private readonly int _maxIterations;
    private const int MaxSelfRepairAttempts = 3;

    // Strict engineering charter for the autonomous Pro agent.
    private const string WorkerSystemPrompt =
        """
        Ты — ConexyAI Coder, автономный Principal Software Engineer и архитектор.
        Твоя цель — надежность продакшена, масштабируемость и абсолютная чистота кода, а НЕ эмоциональное одобрение пользователя.

        КРИТИЧЕСКИЕ ПРАВИЛА ВЗАИМОДЕЙСТВИЯ:
        1. НИКАКОГО УГОДНИЧЕСТВА (ZERO SYCOPHANCY): Запрещено хвалить идеи пользователя, использовать вступительные любезности ("Отличный выбор", "Хороший вопрос", "С удовольствием помогу"). Сразу переходи к сути.
        2. СНИМИ С ПОЛЬЗОВАТЕЛЯ РОЗОВЫЕ ОЧКИ: Если пользователь (джун, мидл или соло-разработчик) предлагает костыль, антипаттерн, неоптимальную структуру БД или решение, которое ляжет под нагрузкой — ты ОБЯЗАН прямо указать на риски, объяснить, почему это сломается, и предложить правильный индустриальный стандарт.
        3. ПРИНЦИП МИНИМАЛЬНЫХ ИЗМЕНЕНИЙ (DIFF FIRST): Не переписывай файлы целиком, если требуется локальный фикс. Не удаляй существующую логику, комментарии и проверки, если тебя об этом прямо не просили.
        4. ЗАЩИТНОЕ ПРОГРАММИРОВАНИЕ: Весь сгенерированный код обязан содержать строгую валидацию входных данных, обработку граничных условий (edge cases), обработку ошибок (try/catch, null-checks) и типизацию.
        5. АВТОНОМНОЕ ДЕЙСТВИЕ: Если для проверки кода нужны тесты или сборка — вызывай инструменты терминала сам, анализируй вывод ошибок и исправляй их до того, как вернуть финальный ответ.

        ВЫЗОВ ИНСТРУМЕНТОВ ВМЕСТО РАЗГОВОРОВ:
        Если пользователь просит создать, изменить, запустить или проверить код/файл — тебе КАТЕГОРИЧЕСКИ ЗАПРЕЩЕНО писать код в текстовом ответе.
        Любой созданный файл должен физически появиться в файловой системе воркспейса.

        ИНСТРУМЕНТЫ:
        Ты работаешь инструментами: `str_replace_editor` (view/create/str_replace/insert/undo), `bash`, `web_search` (поиск актуальной информации в интернете) и системная память задачи (todo-список).

        ## Правила использования инструментов

        1. **Перед любой правкой существующего файла — сначала `view`.** Никогда не вызывай
           `str_replace` с `old_str`, который ты не видел в актуальном содержимом файла в этой
           сессии. Если файл мог измениться после последнего просмотра (например, после
           `bash`-команды типа `git pull`, кодогенерации, или прошлой правки другого файла,
           которая могла затронуть этот) — сделай `view` заново.

        2. **`old_str` должен быть минимальным, но однозначным.** Начинай с наименьшего
           уникального фрагмента. Если получаешь `ambiguous_match` — расширь `old_str`
           окружающими строками (по тексту ошибки, где указаны номера строк совпадений),
           не переключайся на полную перезапись файла.

        3. **Если получаешь `no_match`** — не гадай и не пробуй заменить похожий фрагмент
           вслепую. Сделай `view` файла заново (содержимое могло измениться, либо ты
           неверно вспомнил отступы/форматирование) и сформируй `old_str` заново из
           актуального текста.

        4. **`bash` — только для сборки, тестов, git, пакетных менеджеров и файловых утилит
           (ls, grep, find).** Не используй `bash` для редактирования содержимого файлов
           (`sed`, `echo >>`, heredoc в файл) — для этого есть `str_replace_editor`. Это
           правило существует, чтобы Live Action Status корректно отражал изменения файлов
           в UI.

        5. **После каждой значимой правки — собери проект** (`bash: dotnet build` для
           backend, соответствующий build/lint для frontend), прежде чем переходить к
           следующему шагу. Не накапливай несколько непроверенных правок подряд —
           ошибка компиляции должна быть поймана как можно ближе к причине.

        6. **Используй `todo_write` для многошаговых задач.** Перед началом работы над задачей
           из нескольких шагов — вызови `todo_write` со всеми шагами в статусе `pending`,
           кроме первого (`in_progress`). После завершения каждого шага — вызови `todo_write`
           заново с обновлённым статусом этого шага (`completed`) и следующего (`in_progress`).
           Если шаг оказался не нужен — отметь `skipped` и обязательно укажи `skip_reason`.
           Для однократной простой правки (один файл, одна правка) `todo_write` не обязателен.

        7. **Не используй `file_write` (legacy-инструмент).** Он оставлен в системе для
           обратной совместимости, но для правки файлов используй только `str_replace_editor`.

        8. **При работе с undo:** используй `undo` только для отката твоей же последней
           операции над файлом в этой сессии (например, если после правки `bash: dotnet build`
           показал, что твой подход был неверным с самого начала — не патчи поверх ошибки,
           а откати и сделай заново правильно). Не используй `undo` как замену аккуратному
           планированию правки.

        9. **Не используй `terminal_exec` (legacy-инструмент).** Он оставлен в системе для
           обратной совместимости, но для тебя доступен только `bash` — его вывод виден
           пользователю в терминальной панели в реальном времени, в отличие от `terminal_exec`.

        10. **Используй `web_search` для актуальной информации — это ОБЯЗАТЕЛЬНО, а не опционально.**
            Если вопрос касается конкретного продукта/устройства (существует ли он, дата выхода,
            характеристики, цена), текущих дат/времени, недавних событий, новостей, версий пакетов
            или документации API/библиотек — ты ОБЯЗАН сначала вызвать `web_search` и проверить, а не
            отвечать по памяти. Категорически запрещено писать «такого устройства/продукта не
            существует», если ты не проверил это через `web_search` прямо сейчас. Сомневаешься —
            всё равно вызови `web_search`.

        ## ПРАВИЛА РАБОТЫ С ДОКУМЕНТАМИ И БАЗОЙ ЗНАНИЙ (RAG):

        1. Если вопрос пользователя касается специфичных данных, регламентов, документации, договоров, кода или загруженных файлов — ТЫ ОБЯЗАН СНАЧАЛА вызвать инструмент `search_documents`.
        2. Запрещено придумывать или гадать факты, если они должны содержаться в документах.
        3. Формируй поисковый запрос `query` ёмко, выделяя ключевые сущности и термины (не копируй вежливые слова пользователя вроде «подскажи пожалуйста»).
        4. Если результаты первого поиска не дали точного ответа, переформулируй запрос или вызови поиск с синонимами (до 2 попыток).
        5. При формировании финального ответа обязательно указывай источник: [Название документа, раздел/страница].
        6. Если в найденных документах нет информации для ответа, прямо заяви: «В предоставленных документах нет информации по данному вопросу», не додумывая от себя.

        ## Формат ответа пользователю
        После завершения задачи — короткое резюме: какие файлы изменены, что показала
        финальная сборка/тесты, есть ли известные ограничения или следующие шаги.
        Не дублируй Live Action Status ленту текстом — пользователь уже видел прогресс
        в реальном времени.
        Используй Markdown: **жирный** для ключевых фактов/цифр/названий, заголовки `##`
        для структурирования длинных ответов, списки (`-` или `1.`) для перечислений.
        Команды для выполнения в терминале ВСЕГДА оформляй как код-блок (тройные бэктики с
        указанием языка, например ```powershell или ```bash), а не как инлайн-код (одинарные
        бэктики) внутри предложения — даже если это одна короткая команда.
        """;

    private const string CriticSystemPrompt =
        "You are a ruthless tech lead and security auditor. Review the task and the workspace changes produced by another agent. " +
        "Hunt for race conditions, memory leaks, SQL/DI vulnerabilities, broken edge cases, and violations of the stated requirements.\n\n" +
        "Respond with strict JSON only (no prose, no markdown fences):\n" +
        "{\"verdict\":\"APPROVED\"}\n" +
        "or\n" +
        "{\"verdict\":\"REJECT\",\"issues\":[\"issue one\",\"issue two\"]}\n\n" +
        "Return APPROVED only if the code is correct, safe, and complete. Otherwise return REJECT with a concise, actionable list.";

    private const string CorrectionPrompt =
        "[Build/Execution Failed]: Analyze the compiler/runtime errors above, inspect the broken files, and apply a patch to fix them. Do not report completion until the build/tests pass.";

    private static readonly List<object> AvailableTools = BuildToolSchemas();

    public ConexyAgentRunner(
        IConexyWorkspaceService workspaceService,
        IConexyVisionService visionService,
        IConexyLlmClient llmClient,
        IConexyGitHubService githubService,
        IConexyEditorService editorService,
        IConexyBashService bashService,
        IConexyTodoService todoService,
        IWebSearchService webSearchService,
        IHubContext<ConexyHub> hubContext,
        IDangerousCommandClassifier dangerousCommandClassifier,
        IPendingActionService pendingActionService,
        ISubscriptionService subscriptionService,
        IUserMemoryService memoryService,
        IDocumentService documentService,
        IOptions<AgentOptions> agentOptions)
    {
        _workspaceService = workspaceService;
        _visionService = visionService;
        _llmClient = llmClient;
        _githubService = githubService;
        _editorService = editorService;
        _bashService = bashService;
        _todoService = todoService;
        _webSearchService = webSearchService;
        _hubContext = hubContext;
        _dangerousCommandClassifier = dangerousCommandClassifier;
        _pendingActionService = pendingActionService;
        _subscriptionService = subscriptionService;
        _memoryService = memoryService;
        _documentService = documentService;

        var configured = agentOptions.Value.MaxIterations;
        _maxIterations = configured <= 0 ? 15 : configured;
    }

    public async Task<string> RunLoopAsync(ConexyJob job, CancellationToken ct = default)
    {
        _job = job;
        // taskId = per-run id (SignalR group, logging, todo, dedup).
        // chatId = stable per-conversation id (workspace files, editor undo, bash).
        var taskId = job.TaskId;
        var chatId = job.ChatId;

        var group = _hubContext.Clients.Group($"task_{taskId}");
        await group.SendAsync("OnLog", "[Agent Initialized] Processing user request...", ct);

        // SUBSCRIPTION_TIERS: добавлено 2026-09-17 — inject durable user memory facts.
        var systemPrompt = WorkerSystemPrompt;
        // INCOGNITO_CHAT: добавлено 2026-09-20 — kept for parity with the chat path; the UI only
        // enables incognito for the Chat tab today, but a flag that is set must never leak memory.
        if (!job.Incognito)
        {
            var memoryBlock = await _memoryService.BuildPromptBlockAsync(job.UserId, ct);
            if (!string.IsNullOrEmpty(memoryBlock))
                systemPrompt += memoryBlock;
        }

        var messages = new List<ChatMessage>
        {
            new("system", systemPrompt),
            ChatMessageFactory.User(job.Prompt, job.Attachments)
        };

        var lastCommandFailed = false;
        var failedBuildAttempts = 0;
        var changedFiles = new HashSet<string>(StringComparer.Ordinal);
        var executedCommands = new List<string>();

        for (var step = 1; step <= _maxIterations; step++)
        {
            ct.ThrowIfCancellationRequested();

            var thinkingLabel = step == 1
                ? "Анализирую задачу и архитектуру..."
                : "Обдумываю следующий шаг...";
            await SendAgentStatusAsync(taskId, "thinking", thinkingLabel, ct: ct);

            var llmTurn = await StreamAgentTurnAsync(taskId, messages, ct);
            var responseMessage = llmTurn.Message;
            // SUBSCRIPTION_TIERS: добавлено 2026-09-17 — internal agent tokens count toward the agent budget.
            await _subscriptionService.RecordAgentTokensAsync(job.UserId, llmTurn.TotalTokens, ct);
            messages.Add(responseMessage);

            if (responseMessage.ToolCalls == null || responseMessage.ToolCalls.Count == 0)
            {
                // The worker may not finalize while a build/test is still red.
                if (lastCommandFailed)
                {
                    messages.Add(new ChatMessage("system", CorrectionPrompt));
                    continue;
                }

                var finalText = ExtractTextContent(responseMessage);

                // Maker-Checker only applies to real code changes. A pure dialog reply
                // must be returned verbatim instead of being swallowed by the auditor.
                if (changedFiles.Count > 0)
                {
                    var audit = await ReviewWorkspaceAsync(chatId, job.Prompt, ct);
                    if (!audit.Approved)
                    {
                        await group.SendAsync("OnLog", $"[Auditor] REJECT — {audit.Issues.Count} issue(s) found. Sending back for rework.", ct);
                        messages.Add(new ChatMessage("system",
                            "[Auditor REJECT] The reviewer found the following problems. Fix every item, then re-run verification:\n- " +
                            string.Join("\n- ", audit.Issues)));
                        continue;
                    }

                    await group.SendAsync("OnLog", "[Auditor] APPROVED — applying changes.", ct);
                }

                // STREAM_TOKENS: добавлено 2026-09-20 — the answer already reached the client
                // token by token while the turn streamed, so only a turn that produced no text
                // at all needs a fallback line here.
                var answer = string.IsNullOrWhiteSpace(finalText)
                    ? "Готово. Задача выполнена."
                    : finalText;
                await SendAgentStatusAsync(taskId, "idle", "Готово", ct: ct);
                if (string.IsNullOrWhiteSpace(finalText))
                {
                    await group.SendAsync("OnContentToken", answer, ct);
                }

                await group.SendAsync("OnLog", "[Agent Completed] Solution finalized.", ct);

                // No file modifications were made: the model's text is the whole answer.
                if (changedFiles.Count == 0)
                {
                    return answer;
                }

                // File changes were made: return the final report plus a summary card.
                return BuildFinalReport(finalText, changedFiles, executedCommands);
            }

            var failedThisIteration = false;
            foreach (var toolCall in responseMessage.ToolCalls)
            {
                RecordToolEffect(toolCall, changedFiles, executedCommands);

                await group.SendAsync("OnLog", $"[Tool Call] Executing {toolCall.Function.Name}...", ct);

                ConexyToolResult result;
                if (toolCall.Function.Name == "take_screenshot")
                {
                    result = await HandleScreenshotAsync(taskId, toolCall, messages, group, ct);
                }
                else
                {
                    result = await DispatchToolAsync(taskId, chatId, toolCall, ct);
                }

                // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
                // A rejected dangerous command was never executed, so it must not appear
                // in the final "Commands executed" summary.
                if (toolCall.Function.Name == "bash" && result.Output.StartsWith("USER_REJECTED:", StringComparison.OrdinalIgnoreCase))
                {
                    var rejectedCommand = ReadCommandArgument(toolCall);
                    if (!string.IsNullOrEmpty(rejectedCommand))
                        executedCommands.RemoveAll(c => c == rejectedCommand);
                }

                messages.Add(new ChatMessage(
                    Role: "tool",
                    Content: result.Output,
                    ToolCallId: toolCall.Id
                ));

                if (toolCall.Function.Name == "terminal_exec")
                {
                    lastCommandFailed = result.IsError;
                    if (result.IsError)
                    {
                        failedThisIteration = true;
                        failedBuildAttempts++;
                        if (failedBuildAttempts > MaxSelfRepairAttempts)
                        {
                            throw new InvalidOperationException("Build/execution failed repeatedly; exceeding the 3-attempt self-repair limit.");
                        }
                    }
                    else
                    {
                        failedBuildAttempts = 0;
                    }
                }
            }

            if (failedThisIteration)
            {
                messages.Add(new ChatMessage("system", CorrectionPrompt));
            }
        }

        if (lastCommandFailed)
        {
            throw new InvalidOperationException("Agent could not resolve build/execution errors within the iteration limit.");
        }

        return "Task reached maximum autonomous iteration limit.";
    }

    private async Task<ConexyToolResult> DispatchToolAsync(Guid taskId, Guid chatId, LlmToolCall toolCall, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolCall.Function.Arguments);
            var root = doc.RootElement;

            switch (toolCall.Function.Name)
            {
                case "file_write":
                {
                    var path = GetString(root, "path");
                    var content = GetString(root, "content");
                    if (string.IsNullOrEmpty(path))
                        return new ConexyToolResult(toolCall.Id, "file_write requires 'path'.", true);

                    await SendAgentStatusAsync(taskId, "writing", $"Создаю файл {Path.GetFileName(path)}...", file: path, ct: ct);

                    var res = await _workspaceService.WriteFileAsync(chatId, path, content, ct);
                    if (res.Success)
                    {
                        await SendFileCreatedAsync(taskId, path, ct);
                    }
                    await LogAsync(taskId, $"[File Written] {path}", ct);
                    return new ConexyToolResult(toolCall.Id, res.Success ? $"File '{path}' written." : res.Error!, !res.Success);
                }

                case "file_read":
                {
                    var path = GetString(root, "path");
                    if (string.IsNullOrEmpty(path))
                        return new ConexyToolResult(toolCall.Id, "file_read requires 'path'.", true);

                    var res = await _workspaceService.ReadFileAsync(chatId, path, ct);
                    return new ConexyToolResult(toolCall.Id, res.Success ? res.Content! : res.Error!, !res.Success);
                }

                case "file_patch":
                {
                    var path = GetString(root, "path");
                    var searchBlock = GetString(root, "search_block");
                    var replaceBlock = GetString(root, "replace_block");
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(searchBlock))
                        return new ConexyToolResult(toolCall.Id, "file_patch requires 'path' and 'search_block'.", true);

                    await SendAgentStatusAsync(taskId, "writing", $"Правлю файл {Path.GetFileName(path)}...", file: path, ct: ct);

                    var res = await _workspaceService.PatchFileAsync(chatId, path, searchBlock, replaceBlock, ct);
                    if (res.Success)
                    {
                        await SendFileCreatedAsync(taskId, path, ct);
                    }
                    await LogAsync(taskId, $"[File Patched] {path}", ct);
                    return new ConexyToolResult(toolCall.Id, res.Success ? $"Patched '{path}'." : res.Error!, !res.Success);
                }

                case "workspace_list_files":
                {
                    var relativeDirectory = Optional(root, "path") ?? string.Empty;
                    var res = await _workspaceService.ListFilesAsync(chatId, relativeDirectory, ct);
                    if (!res.Success)
                        return new ConexyToolResult(toolCall.Id, res.Error!, true);

                    var listing = res.Files.Count == 0
                        ? "Workspace is empty."
                        : string.Join("\n", res.Files.OrderBy(f => f, StringComparer.Ordinal));
                    return new ConexyToolResult(toolCall.Id, listing, false);
                }

                case "terminal_exec":
                {
                    var command = GetString(root, "command");
                    var workingDirectory = Optional(root, "working_directory");
                    if (string.IsNullOrEmpty(command))
                        return new ConexyToolResult(toolCall.Id, "terminal_exec requires 'command'.", true);

                    await SendAgentStatusAsync(taskId, "executing", $"Выполняю команду в терминале: {command}...", ct: ct);

                    var res = await _workspaceService.ExecuteCommandAsync(chatId, command, workingDirectory, ct);
                    var output = string.IsNullOrWhiteSpace(res.StdErr)
                        ? res.StdOut
                        : $"Stdout: {res.StdOut}\nStderr: {res.StdErr}";
                    await LogAsync(taskId, $"[Command Executed] {command} (Exit Code: {res.ExitCode})", ct);
                    return new ConexyToolResult(toolCall.Id, output, !res.Success);
                }

                case "github_action":
                {
                    return await DispatchGithubActionAsync(toolCall, root, ct);
                }

                case "str_replace_editor":
                {
                    var request = JsonSerializer.Deserialize<StrReplaceEditorRequest>(toolCall.Function.Arguments);
                    if (request is null || string.IsNullOrWhiteSpace(request.Command) || string.IsNullOrWhiteSpace(request.Path))
                        return new ConexyToolResult(toolCall.Id, "str_replace_editor requires 'command' and 'path'.", true);

                    var res = await _editorService.ExecuteAsync(chatId, request, ct);
                    var output = res.Success
                        ? res.Output ?? "Done."
                        : $"{res.ErrorType}: {res.ErrorDetail}";
                    return new ConexyToolResult(toolCall.Id, output, !res.Success);
                }

                case "bash":
                {
                    var request = JsonSerializer.Deserialize<BashToolRequest>(toolCall.Function.Arguments);
                    if (request is null || string.IsNullOrWhiteSpace(request.Command))
                        return new ConexyToolResult(toolCall.Id, "bash requires 'command'.", true);

                    // COMMAND_CONFIRM: добавлено 2026-09-20 — раньше подтверждение требовалось
                    // только для команд из DangerousCommandClassifier. Теперь через него идёт любая
                    // bash-команда, а классификатор остался только для визуального акцента.
                    var isDangerous = _dangerousCommandClassifier.IsDangerous(request.Command);
                    return await RunBashWithConfirmationAsync(taskId, chatId, toolCall, request, isDangerous, ct);
                }

                case "todo_write":
                {
                    var request = JsonSerializer.Deserialize<TodoWriteRequest>(toolCall.Function.Arguments);
                    if (request is null || request.Todos is null)
                        return new ConexyToolResult(toolCall.Id, "todo_write requires a 'todos' list.", true);

                    var res = await _todoService.WriteAsync(taskId, request, ct);
                    var output = res.Success
                        ? $"Todo list updated ({request.Todos.Count} steps)."
                        : $"{res.ErrorType}: {res.ErrorDetail}";
                    return new ConexyToolResult(toolCall.Id, output, !res.Success);
                }

                case "web_search":
                {
                    var request = JsonSerializer.Deserialize<WebSearchRequest>(toolCall.Function.Arguments);
                    if (request is null || string.IsNullOrWhiteSpace(request.Query))
                        return new ConexyToolResult(toolCall.Id, "web_search requires a 'query'.", true);

                    await SendAgentStatusAsync(taskId, "searching", $"Ищу в интернете: {request.Query}...", ct: ct);

                    var res = await _webSearchService.SearchAsync(request.Query, ct);
                    return new ConexyToolResult(toolCall.Id, res.Output, !res.Success);
                }

                // RAG: добавлено 2026-09-17
                case "search_documents":
                {
                    var request = JsonSerializer.Deserialize<SearchDocumentsRequest>(toolCall.Function.Arguments);
                    if (request is null || string.IsNullOrWhiteSpace(request.Query))
                        return new ConexyToolResult(toolCall.Id, "search_documents requires 'query'.", true);

                    await SendAgentStatusAsync(taskId, "searching", $"Ищу в документах: {request.Query}...", ct: ct);

                    var json = await _documentService.SearchJsonAsync(_job.UserId, request.Query, request.Limit ?? 5, request.DocumentName, ct);
                    return new ConexyToolResult(toolCall.Id, json, false);
                }

                case "read_document_chunk":
                {
                    var request = JsonSerializer.Deserialize<ReadDocumentChunkRequest>(toolCall.Function.Arguments);
                    if (request is null || string.IsNullOrWhiteSpace(request.DocumentId) || !Guid.TryParse(request.DocumentId, out var documentId))
                        return new ConexyToolResult(toolCall.Id, "read_document_chunk requires a valid 'document_id'.", true);

                    var json = await _documentService.ReadChunkJsonAsync(_job.UserId, documentId, request.ChunkIndex, ct);
                    return new ConexyToolResult(toolCall.Id, json, false);
                }

                default:
                    return new ConexyToolResult(toolCall.Id, $"Unknown tool '{toolCall.Function.Name}'.", true);
            }
        }
        catch (Exception ex)
        {
            return new ConexyToolResult(toolCall.Id, $"Execution error: {ex.Message}", true);
        }
    }

    // COMMAND_CONFIRM: расширено 2026-09-20 — подтверждение требуется для любой bash-команды.
    /// <summary>
    /// Runs a <c>bash</c> command after the user approves it. Emits a
    /// <c>pending_confirmation</c> tool action, blocks on the pending-action service, and
    /// then either executes normally or returns a <c>USER_REJECTED</c> result so the model
    /// can propose an alternative without stopping the whole task. When the task is
    /// auto-approved ("allow all for this session") the wait is skipped entirely.
    /// </summary>
    private async Task<ConexyToolResult> RunBashWithConfirmationAsync(
        Guid taskId,
        Guid chatId,
        LlmToolCall toolCall,
        BashToolRequest request,
        bool isDangerous,
        CancellationToken ct)
    {
        // Propagated into every ToolAction the bash service emits, so the feed keeps the accent.
        request.IsDangerous = isDangerous;

        // "Allow all for this task": the user already approved everything for this run, so the
        // command executes straight away and shows up as an ordinary action row.
        if (_pendingActionService.IsTaskAutoApproved(taskId))
        {
            await SendAgentStatusAsync(taskId, "executing", $"Выполняю команду: {request.Command}...", ct: ct);
            var auto = await _bashService.ExecuteAsync(chatId, request, emitStartEvent: true, ct: ct);
            var autoOutput = string.IsNullOrEmpty(auto.Output)
                ? auto.ErrorType ?? "command failed"
                : auto.Output;
            return new ConexyToolResult(toolCall.Id, autoOutput, !auto.Success);
        }

        var actionId = Guid.NewGuid();
        var workingDirectory = _workspaceService.GetTaskWorkspacePath(chatId);

        await _hubContext.Clients.Group($"task_{taskId}").SendAsync("ToolAction", new ToolActionEvent
        {
            ToolName = "bash",
            Command = request.Command,
            Path = "",
            Status = "pending_confirmation",
            Summary = $"Ожидает подтверждения: {request.Command}",
            WorkingDirectory = workingDirectory,
            PendingActionId = actionId,
            IsDangerous = isDangerous
        }, ct);

        bool approved;
        try
        {
            approved = await _pendingActionService.WaitForDecisionAsync(
                actionId, _job.UserId, chatId, taskId, request.Command, workingDirectory, isDangerous, ct);
        }
        catch (OperationCanceledException)
        {
            return new ConexyToolResult(toolCall.Id, "CANCELLED: подтверждение команды отменено (таймаут/остановка).", true);
        }

        if (!approved)
        {
            await _hubContext.Clients.Group($"task_{taskId}").SendAsync("ToolAction", new ToolActionEvent
            {
                ToolName = "bash",
                Command = request.Command,
                Path = "",
                Status = "rejected",
                Summary = "Отклонено пользователем",
                WorkingDirectory = workingDirectory,
                PendingActionId = actionId,
                IsDangerous = isDangerous
            }, ct);

            return new ConexyToolResult(
                toolCall.Id,
                $"USER_REJECTED: команда '{request.Command}' отклонена пользователем. Предложи альтернативный способ или продолжи без неё.",
                false);
        }

        // Correlate the completion ToolAction with the confirmation card via the action id.
        request.PendingActionId = actionId;
        var res = await _bashService.ExecuteAsync(chatId, request, emitStartEvent: false, ct: ct);
        var output = string.IsNullOrEmpty(res.Output)
            ? res.ErrorType ?? "command failed"
            : res.Output;
        return new ConexyToolResult(toolCall.Id, output, !res.Success);
    }

    // STREAM_TOKENS: добавлено 2026-09-20
    /// <summary>
    /// Runs one agent turn as a streamed completion and forwards every upstream delta to the
    /// client immediately, so the coder model answers token by token like flash/pro instead of
    /// arriving as one block. Content, reasoning and the assembled tool calls are returned as a
    /// single assistant message so the rest of the loop is unchanged.
    /// </summary>
    private async Task<(ChatMessage Message, int TotalTokens)> StreamAgentTurnAsync(
        Guid taskId,
        List<ChatMessage> messages,
        CancellationToken ct)
    {
        var group = _hubContext.Clients.Group($"task_{taskId}");
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        List<LlmToolCall>? toolCalls = null;
        var totalTokens = 0;
        var yieldedAnyDelta = false;

        var stream = _llmClient
            .StreamChatAsync(messages, _job.ModelType, _job.ReasoningEffort, AvailableTools, taskId: _job.TaskId, ct: ct)
            .GetAsyncEnumerator(ct);

        await using (stream)
        {
            while (true)
            {
                StreamDelta delta;
                try
                {
                    if (!await stream.MoveNextAsync())
                        break;
                    delta = stream.Current;
                }
                catch (Exception ex) when (!yieldedAnyDelta && !ct.IsCancellationRequested)
                {
                    // Streaming is an optimisation, not a requirement: if the upstream rejects or
                    // breaks the stream before the first token, fall back to a plain completion so
                    // the agent still works (just without incremental output).
                    await group.SendAsync("OnLog", $"[Stream] Incremental output unavailable ({ex.Message}); falling back to a plain completion.", ct);
                    var fallback = await _llmClient.SendChatAsync(
                        _job.ModelType, messages, AvailableTools, _job.ReasoningEffort, _job.TaskId, ct);
                    return (fallback.Message, fallback.TotalTokens);
                }

                yieldedAnyDelta = true;

                if (!string.IsNullOrEmpty(delta.Reasoning))
                {
                    reasoning.Append(delta.Reasoning);
                    await group.SendAsync("OnThinkingToken", delta.Reasoning, ct);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    content.Append(delta.Content);
                    // Forwarded as it arrives: no aggregation, no buffering.
                    await group.SendAsync("OnContentToken", delta.Content, ct);
                }

                if (delta.ToolCalls is { Count: > 0 })
                {
                    toolCalls = delta.ToolCalls;
                }

                if (delta.TotalTokens is > 0)
                {
                    totalTokens = delta.TotalTokens.Value;
                }
            }
        }

        if (totalTokens <= 0)
        {
            // The upstream did not report usage for this streamed turn. Approximate from the
            // payload so the agent token budget still accrues instead of never being enforced.
            totalTokens = EstimateTokens(content.Length, reasoning.Length, toolCalls);
        }

        var message = new ChatMessage(
            "assistant",
            content.Length > 0 ? content.ToString() : null,
            toolCalls,
            null,
            reasoning.Length > 0 ? reasoning.ToString() : null);

        return (message, totalTokens);
    }

    /// <summary>
    /// Rough ~4 chars/token fallback used only when a streamed turn reports no usage.
    /// </summary>
    private static int EstimateTokens(int contentChars, int reasoningChars, List<LlmToolCall>? toolCalls)
    {
        var toolCallChars = toolCalls?.Sum(tc => tc.Function.Name.Length + tc.Function.Arguments.Length) ?? 0;
        return (contentChars + reasoningChars + toolCallChars + 3) / 4;
    }

    private async Task<ConexyToolResult> HandleScreenshotAsync(
        Guid taskId,
        LlmToolCall toolCall,
        List<ChatMessage> messages,
        Microsoft.AspNetCore.SignalR.IClientProxy group,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(toolCall.Function.Arguments);
        var root = doc.RootElement;

        var url = GetString(root, "url");
        if (string.IsNullOrEmpty(url))
            return new ConexyToolResult(toolCall.Id, "take_screenshot requires 'url'.", true);

        var width = GetInt(root, "viewport_width", 1280);
        var height = GetInt(root, "viewport_height", 800);

        var base64 = await _visionService.CaptureScreenshotBase64Async(url, width, height, ct);
        await group.SendAsync("OnScreenshot", base64, ct);

        // Feed the image back into the multimodal context so the Pro model can audit layout.
        messages.Add(ChatMessageFactory.User(
            "Screenshot of the rendered page. Visually audit the layout, spacing, alignment and responsive defects.",
            new List<TaskAttachment> { new("screenshot.jpg", base64, "image/jpeg") }));

        return new ConexyToolResult(toolCall.Id, "Screenshot captured and attached for visual analysis.", false);
    }

    private async Task<ConexyToolResult> DispatchGithubActionAsync(LlmToolCall toolCall, JsonElement root, CancellationToken ct)
    {
        var operation = GetString(root, "operation");
        var token = ResolveGitHubToken();

        if (string.IsNullOrWhiteSpace(token))
            return new ConexyToolResult(toolCall.Id, "github_action requires a GitHub PAT (set GitHub:PersonalAccessToken or GITHUB_PAT).", true);

        switch (operation)
        {
            case "clone_repo":
            {
                var repoUrl = GetString(root, "repo_url");
                if (string.IsNullOrWhiteSpace(repoUrl))
                    return new ConexyToolResult(toolCall.Id, "clone_repo requires 'repo_url'.", true);

                var targetFolder = Optional(root, "target_folder");
                var branch = Optional(root, "branch");
                var res = await _workspaceService.GitCloneAsync(_job.ChatId, repoUrl, token, targetFolder, branch, ct);
                await LogAsync(_job.TaskId, $"[Git Clone] {repoUrl}", ct);
                return new ConexyToolResult(toolCall.Id, res.Success ? res.Message! : res.Error!, !res.Success);
            }

            case "create_branch":
            {
                var branchName = GetString(root, "branch_name");
                if (string.IsNullOrWhiteSpace(branchName))
                    return new ConexyToolResult(toolCall.Id, "create_branch requires 'branch_name'.", true);

                var res = await _workspaceService.GitCreateBranchAsync(_job.ChatId, branchName, ct);
                await LogAsync(_job.TaskId, $"[Git Branch] {branchName}", ct);
                return new ConexyToolResult(toolCall.Id, res.Success ? res.Message! : res.Error!, !res.Success);
            }

            case "commit_and_push":
            {
                var commitMessage = GetString(root, "commit_message");
                var branch = GetString(root, "branch");
                if (string.IsNullOrWhiteSpace(commitMessage) || string.IsNullOrWhiteSpace(branch))
                    return new ConexyToolResult(toolCall.Id, "commit_and_push requires 'commit_message' and 'branch'.", true);

                var repo = Optional(root, "repo") ?? Optional(root, "repo_url");
                var changedFiles = GetStringArray(root, "changed_files");

                var res = await _workspaceService.GitCommitPushAsync(_job.ChatId, commitMessage, branch, token, repo, changedFiles, ct);
                await LogAsync(_job.TaskId, $"[Git Commit & Push] branch={branch}", ct);
                return new ConexyToolResult(toolCall.Id, res.Success ? res.Message! : res.Error!, !res.Success);
            }

            case "create_pull_request":
            {
                var title = GetString(root, "title");
                if (string.IsNullOrWhiteSpace(title))
                    return new ConexyToolResult(toolCall.Id, "create_pull_request requires 'title'.", true);

                var body = Optional(root, "body");
                var headBranch = Optional(root, "head_branch") ?? Optional(root, "branch");
                var baseBranch = GetString(root, "base_branch", "main");
                var repo = Optional(root, "repo") ?? Optional(root, "repo_url");

                if (string.IsNullOrWhiteSpace(headBranch))
                    return new ConexyToolResult(toolCall.Id, "create_pull_request requires 'head_branch'.", true);

                var res = await _githubService.CreatePullRequestAsync(_job.ChatId, token, title, body, headBranch, baseBranch, repo, ct);
                await LogAsync(_job.TaskId, $"[Git PR] {headBranch} -> {baseBranch}", ct);

                var message = res.Success ? res.Message! : res.Error!;
                if (!string.IsNullOrWhiteSpace(res.PullRequestUrl))
                    message += $" URL: {res.PullRequestUrl}";

                return new ConexyToolResult(toolCall.Id, message, !res.Success);
            }

            default:
                return new ConexyToolResult(toolCall.Id, $"Unknown github_action operation '{operation}'.", true);
        }
    }

    private async Task<AuditVerdict> ReviewWorkspaceAsync(Guid chatId, string taskPrompt, CancellationToken ct)
    {
        var files = await _workspaceService.ListFilesAsync(chatId, "", ct);
        if (!files.Success || files.Files.Count == 0)
        {
            return AuditVerdict.ApprovedVerdict;
        }

        var builder = new StringBuilder();
        builder.AppendLine("## Task");
        builder.AppendLine(taskPrompt);
        builder.AppendLine();
        builder.AppendLine("## Workspace changes");
        foreach (var file in files.Files.Take(60))
        {
            builder.AppendLine($"### {file}");
            var read = await _workspaceService.ReadFileAsync(chatId, file, ct);
            if (read.Success && !string.IsNullOrWhiteSpace(read.Content))
            {
                builder.AppendLine(Truncate(read.Content, 6000));
            }
        }

        var messages = new List<ChatMessage>
        {
            new("system", CriticSystemPrompt),
            new("user", builder.ToString())
        };

        var response = await _llmClient.SendChatAsync(ConexyModelType.ConexyV1Pro, messages, new List<object>(), _job.ReasoningEffort, _job.TaskId, ct);
        // SUBSCRIPTION_TIERS: добавлено 2026-09-17 — critic tokens count toward the agent budget.
        await _subscriptionService.RecordAgentTokensAsync(_job.UserId, response.TotalTokens, ct);
        return ParseAuditVerdict(response.Message.Text ?? string.Empty);
    }

    private static AuditVerdict ParseAuditVerdict(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var root = doc.RootElement;
                var verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() : null;

                var issues = new List<string>();
                if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            issues.Add(el.GetString()!);
                        }
                    }
                }

                if (string.Equals(verdict, "APPROVED", StringComparison.OrdinalIgnoreCase))
                    return AuditVerdict.ApprovedVerdict;
                if (string.Equals(verdict, "REJECT", StringComparison.OrdinalIgnoreCase))
                    return new AuditVerdict(false, issues);
            }
            catch (JsonException)
            {
                // Fall through to the safe default below.
            }
        }

        // A malformed audit must not deadlock the loop.
        return AuditVerdict.ApprovedVerdict;
    }

    private string? ResolveGitHubToken() =>
        !string.IsNullOrWhiteSpace(_job.GitHubToken)
            ? _job.GitHubToken
            : _githubService.GetConfiguredToken();

    private async Task LogAsync(Guid taskId, string message, CancellationToken ct)
    {
        await _hubContext.Clients.Group($"task_{taskId}").SendAsync("OnLog", message, ct);
    }

    private async Task SendAgentStatusAsync(Guid taskId, string stage, string label, string? file = null, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group($"task_{taskId}").SendAsync("OnAgentStatus", new { stage, label, file }, ct);
    }

    private async Task SendFileCreatedAsync(Guid taskId, string path, CancellationToken ct)
    {
        await _hubContext.Clients.Group($"task_{taskId}").SendAsync("OnFileCreated", new { path, name = Path.GetFileName(path) }, ct);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n... (truncated)";

    private static string GetString(JsonElement root, string name, string fallback = "")
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;
    }

    // DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
    private static string ReadCommandArgument(LlmToolCall toolCall)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolCall.Function.Arguments);
            return GetString(doc.RootElement, "command");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string? Optional(JsonElement root, string name)
    {
        var value = GetString(root, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v
            : fallback;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
            }
        }
        return result;
    }

    private static void RecordToolEffect(LlmToolCall toolCall, ISet<string> changedFiles, ICollection<string> commands)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolCall.Function.Arguments);
            var root = doc.RootElement;

            switch (toolCall.Function.Name)
            {
                case "file_write":
                case "file_patch":
                {
                    var path = GetString(root, "path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        changedFiles.Add(path);
                    }
                    break;
                }
                case "terminal_exec":
                case "bash":
                {
                    var command = GetString(root, "command");
                    if (!string.IsNullOrEmpty(command))
                    {
                        commands.Add(command);
                    }
                    break;
                }
                case "str_replace_editor":
                {
                    var command = GetString(root, "command");
                    var path = GetString(root, "path");
                    if (!string.IsNullOrEmpty(path) &&
                        (command == "create" || command == "str_replace" || command == "insert" || command == "undo"))
                    {
                        changedFiles.Add(path);
                    }
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Best-effort bookkeeping; DispatchToolAsync validates arguments authoritatively.
        }
    }

    private static string ExtractTextContent(ChatMessage message)
    {
        return message.Content switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.Array } el => ExtractTextFromBlocks(el),
            _ => string.Empty
        };
    }

    private static string ExtractTextFromBlocks(JsonElement array)
    {
        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            if (item.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "text", StringComparison.Ordinal) &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }
        return string.Join("\n", parts);
    }

    private static string BuildFinalReport(
        string finalText,
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> commands)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(finalText))
        {
            sb.AppendLine(finalText.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("=== Change Summary ===");

        if (changedFiles.Count > 0)
        {
            sb.AppendLine("Files changed:");
            foreach (var file in changedFiles)
            {
                sb.AppendLine($"- {file}");
            }
        }

        if (commands.Count > 0)
        {
            sb.AppendLine("Commands executed:");
            foreach (var command in commands)
            {
                sb.AppendLine($"- {command}");
            }
        }

        return sb.ToString().Trim();
    }

    private record AuditVerdict(bool Approved, IReadOnlyList<string> Issues)
    {
        public static AuditVerdict ApprovedVerdict { get; } = new(true, Array.Empty<string>());
    }

    private static List<object> BuildToolSchemas() => new()
    {
        Function("file_read", "Read the full contents of a workspace file.",
            new { type = "object", properties = new { path = new { type = "string", description = "Relative path to the file within the workspace." } }, required = new[] { "path" } }),
        Function("file_write", "Write or overwrite a workspace file with the complete new content.",
            new { type = "object", properties = new {
                path = new { type = "string", description = "Relative path to the file (e.g. 'index.html', 'src/App.tsx')" },
                content = new { type = "string", description = "Complete content to write" }
            }, required = new[] { "path", "content" } }),
        Function("file_patch", "Apply a minimal context patch: replace a unique 'search_block' with 'replace_block' in a file. Prefer this over file_write for small edits.",
            new { type = "object", properties = new {
                path = new { type = "string", description = "Relative path to the file" },
                search_block = new { type = "string", description = "Exact, unique block of existing content to replace" },
                replace_block = new { type = "string", description = "Replacement content" }
            }, required = new[] { "path", "search_block", "replace_block" } }),
        Function("str_replace_editor", "View and edit workspace files with precise operations (view, create, str_replace, insert, undo). Prefer this over file_write for targeted edits.",
            new { type = "object", properties = new {
                command = new { type = "string", @enum = new[] { "view", "create", "str_replace", "insert", "undo" }, description = "The editor command" },
                path = new { type = "string", description = "Path relative to the session workspace root" },
                file_text = new { type = "string", description = "Full file content (create only)" },
                old_str = new { type = "string", description = "Exact text to replace (str_replace only); must occur exactly once" },
                new_str = new { type = "string", description = "Replacement text (str_replace) or text to insert (insert)" },
                insert_line = new { type = "integer", description = "Line number after which to insert new_str (insert only); 0 = start" },
                view_range = new { type = "array", items = new { type = "integer" }, description = "Optional [start_line, end_line] for view, 1-indexed" }
            }, required = new[] { "command", "path" } }),
        Function("workspace_list_files", "List all files currently present in the workspace (recursively).",
            new { type = "object", properties = new {
                path = new { type = "string", description = "Optional relative directory to list (defaults to workspace root)" }
            }, required = Array.Empty<string>() }),
        Function("bash", "Run a shell command in the isolated session workspace with a timeout. Output longer than 80 lines is truncated (first 40 + last 40). Use for build, tests, package managers, git and file utilities.",
            new { type = "object", properties = new {
                command = new { type = "string", description = "The shell command to run" },
                timeout_seconds = new { type = "integer", description = "Optional timeout in seconds (default 60, max 300)" }
            }, required = new[] { "command" } }),
        Function("todo_write", "Create or update the todo list for the current multi-step task. Pass the FULL list every time (not a delta); this replaces the previous todo state.",
            new { type = "object", properties = new {
                todos = new { type = "array", items = new { type = "object", properties = new {
                    id = new { type = "string", description = "Short stable step id, e.g. '1', '2'" },
                    content = new { type = "string", description = "Step description" },
                    status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed", "skipped" } },
                    skip_reason = new { type = "string", description = "Required when status = skipped" }
                }, required = new[] { "id", "content", "status" } } }
            }, required = new[] { "todos" } }),
        Function("take_screenshot", "Render a URL or local HTML file with headless Chromium and attach the screenshot for visual audit.",
            new { type = "object", properties = new {
                url = new { type = "string", description = "http(s) URL or local file path to render" },
                viewport_width = new { type = "integer", description = "Viewport width in pixels (default 1280)" },
                viewport_height = new { type = "integer", description = "Viewport height in pixels (default 800)" }
            }, required = new[] { "url" } }),
        Function("github_action", "Perform a GitHub workflow operation using the configured PAT.",
            new { type = "object", properties = new {
                operation = new { type = "string", @enum = new[] { "clone_repo", "create_branch", "commit_and_push", "create_pull_request" } },
                repo_url = new { type = "string", description = "Repository URL (owner/repo or full URL)" },
                target_folder = new { type = "string", description = "Target folder for clone_repo" },
                repo = new { type = "string", description = "Repository as owner/repo" },
                branch = new { type = "string", description = "Branch name" },
                branch_name = new { type = "string", description = "Branch name to create" },
                commit_message = new { type = "string", description = "Commit message" },
                changed_files = new { type = "array", items = new { type = "string" }, description = "Files to stage (defaults to all)" },
                title = new { type = "string", description = "Pull request title" },
                body = new { type = "string", description = "Pull request body" },
                head_branch = new { type = "string", description = "PR head branch" },
                base_branch = new { type = "string", description = "PR base branch (default main)" }
            }, required = new[] { "operation" } }),
        // RAG: добавлено 2026-09-17
        Function("search_documents", "Выполняет семантический поиск по загруженным документам, файлам и базе знаний проекта. Возвращает список наиболее релевантных фрагментов (чанков) с их ID, заголовками и кратким содержимым. Используй этот инструмент ПЕРВЫМ, когда пользователь спрашивает о фактах, документах, инструкциях, коде или регламентах.",
            new { type = "object", properties = new {
                query = new { type = "string", description = "Поисковый запрос (оптимизированный под смысл, без лишнего мусора)" },
                limit = new { type = "integer", description = "Максимальное количество чанков (по умолчанию 5)" },
                document_name = new { type = "string", description = "Фильтр по конкретному файлу или расширению (если известно)" }
            }, required = new[] { "query" } }),
        Function("read_document_chunk", "Возвращает расширенный контекст конкретного фрагмента документа или полный текст документа по его document_id / chunk_index. Используй, если фрагмента из поиска недостаточно для точного ответа.",
            new { type = "object", properties = new {
                document_id = new { type = "string", description = "ID документа" },
                chunk_index = new { type = "integer", description = "Индекс конкретного фрагмента (если не указан — вернётся полный текст документа)" }
            }, required = new[] { "document_id" } }),
        WebSearchTool.Schema(),
    };

    private static object Function(string name, string description, object parameters) => new
    {
        type = "function",
        function = new { name, description, parameters }
    };
}

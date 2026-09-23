using System.Text;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Hub;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service.Office;
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
    // COMMAND_APPROVAL: добавлено 2026-09-22 — какие команды вообще требуют подтверждения.
    private readonly ICommandApprovalClassifier _commandApproval;
    private readonly IPendingActionService _pendingActionService;
    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    private readonly ISubscriptionService _subscriptionService;
    // CONVERSATION_SERVICE: добавлено 2026-09-23 — единая сборка контекста и запись хода.
    private readonly IConversationService _conversation;
    // TASK_COMPLETION_DIAGNOSTICS: лог агентского цикла (видно, крутится ли он после готового ответа).
    private readonly ILogger<ConexyAgentRunner> _logger;
    // RAG: добавлено 2026-09-17
    private readonly IDocumentService _documentService;
    private ConexyJob _job = null!;
    // PARTIAL_TURN_PERSIST: добавлено 2026-09-22 — накопленный поток ответа на случай остановки.
    private readonly StringBuilder _streamedOutput = new();

    /// <inheritdoc />
    public string PartialOutput => _streamedOutput.ToString();

    private readonly int _maxIterations;
    private const int MaxSelfRepairAttempts = 3;

    // AUDITOR_BUDGET: добавлено 2026-09-23 — у аудитора не было НИКАКОЙ собственной границы, хотя он
    // идёт уже ПОСЛЕ того, как финальный текст улетел в чат. Единственным пределом был таймаут
    // HTTP-клиента: 180 с на попытку и до 3 повторов на 429/5xx, то есть до ~12 минут «ответ готов, а
    // задача running», после чего исключение ещё и роняло уже законченную задачу в Failed.
    private readonly TimeSpan _auditTimeout;

    // AUDITOR_BUDGET: аудитор смотрит только файлы, которые менял агент. Раньше он брал первые 60
    // файлов из РЕКУРСИВНОГО списка всего воркспейса — после `npm install` это node_modules и .git,
    // то есть огромный промпт из мусора, который и делал вызов модели медленным.
    private const int AuditMaxFiles = 40;
    private const int AuditMaxCharsPerFile = 6000;
    private const int AuditMaxPromptChars = 80_000;

    /// <inheritdoc />
    public string GetSystemPrompt(ConexyModelType modelType) => ProfileFor(modelType).BuildSystemPrompt();

    // COWORK_MODE: добавлено 2026-09-23
    /// <summary>
    /// Everything that distinguishes one agent mode from another. The loop itself — streaming, the
    /// tool-failure budget, history through <see cref="IConversationService"/>, auto-completion, the
    /// auditor timeout — is shared, so a fix to the pipeline applies to every mode at once.
    /// </summary>
    /// <param name="AllowedTools">Tools the mode may call; <c>null</c> means every tool. Enforced at the
    /// single tool call point, not only in the advertised list: a model can call a tool it was not
    /// offered, and Cowork must never reach <c>bash</c>.</param>
    /// <param name="AuditsCode">Whether the Maker-Checker code auditor reviews the changed files. Its
    /// charter is about code (race conditions, injections), which is meaningless for a report.</param>
    /// <param name="IncludesDate">Research answers depend on "now"; the coder charter never needed it.</param>
    private sealed record AgentProfile(
        string Mode,
        string SystemPrompt,
        IReadOnlySet<string>? AllowedTools,
        bool AuditsCode,
        bool IncludesDate)
    {
        public bool Allows(string tool) => AllowedTools is null || AllowedTools.Contains(tool);

        public string BuildSystemPrompt() => IncludesDate
            ? SystemPrompt + $"\n\nТекущая дата и время (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}."
            : SystemPrompt;
    }

    private static readonly AgentProfile CoderProfile = new(
        "coder", WorkerSystemPrompt, AllowedTools: null, AuditsCode: true, IncludesDate: false);

    // COWORK_MODE: no bash/terminal_exec/github/screenshot and no legacy file_* tools — only reading
    // and writing documents in the chat workspace, the user's knowledge base, the web and a plan.
    // With no code execution there is nothing to run in a Docker sandbox: files live in the same
    // per-chat workspace directory the file tools already use.
    private static readonly AgentProfile CoworkProfile = new(
        "cowork",
        CoworkSystemPrompt,
        AllowedTools: new HashSet<string>(StringComparer.Ordinal)
        {
            "str_replace_editor",
            "workspace_list_files",
            "todo_write",
            WebSearchTool.Name,
            "search_documents",
            "read_document_chunk",
            "create_document",
            "read_document_file",
        },
        AuditsCode: false,
        IncludesDate: true);

    private static AgentProfile ProfileFor(ConexyModelType modelType) =>
        modelType == ConexyModelType.ConexyCowork ? CoworkProfile : CoderProfile;

    // Profile of the current run (set in RunLoopAsync).
    private AgentProfile _profile = CoderProfile;

    // COWORK_MODE: добавлено 2026-09-23 — устав режима Cowork (нетехнические задачи).
    private const string CoworkSystemPrompt =
        """
        Ты — ConexyAI Cowork, автономный помощник для нетехнических задач: исследования, аналитика, работа с документами и данными, подготовка отчётов, сводок, писем и планов.
        Твоя цель — довести задачу до конца и оставить результат готовым файлом в рабочей области, а не только текстом в чате.

        ПРИНЦИПЫ:
        1. НИКАКОГО УГОДНИЧЕСТВА: без вступительных любезностей, сразу к сути.
        2. ТОЧНОСТЬ ВАЖНЕЕ ГЛАДКОСТИ: не выдумывай факты, цифры, цитаты и источники. Если данных нет — прямо так и напиши; оценки и допущения явно помечай как допущения.
        3. ИСТОЧНИКИ: всё, что взято из интернета, подкрепляй ссылкой; всё, что взято из документов пользователя, — названием документа и раздела.
        4. ЯЗЫК: пиши на языке пользователя, простым деловым языком, без технического жаргона, если пользователь сам его не использует.
        5. ТЫ НЕ ПИШЕШЬ И НЕ ЗАПУСКАЕШЬ КОД. Терминала у тебя нет. Если задача требует программирования (приложение, скрипт, сборка, запуск), сделай ту часть, что возможна без кода, и прямо предложи переключиться на режим Coder во вкладке «Агент».

        ИНСТРУМЕНТЫ:
        - `web_search` — актуальная информация из интернета. ОБЯЗАТЕЛЕН для фактов, которые могли измениться: цены, даты, новости, компании, продукты, статистика, законы. Для широкой темы делай несколько запросов с разных сторон.
        - `search_documents` и `read_document_chunk` — поиск по документам, которые загрузил пользователь. Используй ПЕРВЫМИ, если вопрос касается его файлов, регламентов, договоров.
        - `str_replace_editor` — создание и правка файлов в рабочей области (`create`, `view`, `str_replace`, `insert`, `undo`). `workspace_list_files` — что уже лежит в рабочей области, включая вложения пользователя.
        - `todo_write` — план многошаговой задачи: перед началом перечисли все шаги, затем отмечай прогресс.
        - `create_document` — готовый файл Word (.docx), Excel (.xlsx) или PowerPoint (.pptx) из Markdown. `read_document_file` — текст .docx/.xlsx/.pptx/.pdf из рабочей области (для них `view` показывает двоичный мусор).

        РЕЗУЛЬТАТ В ФАЙЛАХ:
        - Отчёты, сводки, письма, планы, протоколы — Word (`.docx`) через `create_document`: заголовки, списки, таблицы. Черновики можно держать в Markdown (`.md`).
        - Таблицы и расчёты — Excel (`.xlsx`) через `create_document`: каждая Markdown-таблица становится листом, числа пиши без единиц измерения в ячейке, чтобы их можно было считать.
        - Презентации — PowerPoint (`.pptx`) через `create_document`: каждый заголовок `#`/`##` — слайд, под ним 3–6 коротких пунктов.
        - Называй файлы по смыслу и без пробелов (например `svodka-konkurentov.docx`, `budget-2026.xlsx`).
        - Вложения пользователя уже приходят тебе текстом в сообщении; файлы, загруженные раньше, читай через `read_document_file`.

        ФОРМАТ ОТВЕТА В ЧАТЕ:
        После выполнения — короткое резюме: главные выводы (3–7 пунктов), какие файлы созданы, что осталось непроверенным. Не пересказывай весь файл в чате.
        Используй Markdown: **жирный** для ключевых цифр и фактов, заголовки `##` для длинных ответов, списки для перечислений.
        """;

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
        Для документов: `create_document` создаёт .docx/.xlsx/.pptx из Markdown, `read_document_file` читает текст .docx/.xlsx/.pptx/.pdf (не открывай их через `view` — это двоичные файлы).

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

    // AGENT_TOOL_FAILURES: добавлено 2026-09-23
    // Бюджет ПОДРЯД идущих ошибок инструментов — для ЛЮБОГО инструмента, не только bash-команд.
    // Раньше бюджет был только у bash (`lastCommandFailed` / `failedBuildAttempts`), поэтому инструмент,
    // который в этом окружении не может сработать в принципе (take_screenshot без Chromium), не имел
    // вообще никакой верхней границы — ни на число попыток, ни на завершение прогона.
    // Именно серия, а не сумма: любой успешный вызов сбрасывает счётчик, поэтому обычная отладка
    // (упал тест → поправил → прошёл) его не наберёт, а петля на недоступном инструменте — наберёт.
    private const int ToolFailureBudget = 6;

    // Сколько раз ОДИН инструмент должен упасть, чтобы мы сказали модели «он тут не работает».
    private const int ToolRetryWarningLimit = 3;

    // CONTINUE_GENERATION: the resume instruction moved into ConversationService, which now owns
    // the whole "prefix + continue" composition for every path (it used to be duplicated here and
    // in the chat worker).

    private static readonly List<object> AvailableTools = BuildToolSchemas(includeScreenshot: true);

    // Тот же список без take_screenshot: используется, когда Chromium в окружении недоступен.
    private static readonly List<object> ToolsWithoutScreenshot = BuildToolSchemas(includeScreenshot: false);

    // COWORK_MODE: те же схемы, отфильтрованные по профилю, — описание каждого инструмента одно на все режимы.
    private static readonly List<object> CoworkTools = ToolsWithoutScreenshot
        .Where(schema => CoworkProfile.Allows(ToolName(schema)))
        .ToList();

    // Набор инструментов текущего прогона. Член, а не константа, потому что зависит от окружения
    // (есть ли Chromium) — см. ResolveToolsAsync.
    private List<object> _tools = AvailableTools;

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
        ICommandApprovalClassifier commandApproval,
        IPendingActionService pendingActionService,
        ISubscriptionService subscriptionService,
        IConversationService conversation,
        ILogger<ConexyAgentRunner> logger,
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
        _commandApproval = commandApproval;
        _pendingActionService = pendingActionService;
        _subscriptionService = subscriptionService;
        _conversation = conversation;
        _logger = logger;
        _documentService = documentService;

        var configured = agentOptions.Value.MaxIterations;
        _maxIterations = configured <= 0 ? 15 : configured;

        var auditSeconds = agentOptions.Value.AuditTimeoutSeconds;
        _auditTimeout = TimeSpan.FromSeconds(auditSeconds <= 0 ? 90 : auditSeconds);
    }

    public async Task<string> RunLoopAsync(ConexyJob job, ConversationContext context, CancellationToken ct = default)
    {
        _job = job;
        _profile = ProfileFor(job.ModelType);
        // taskId = per-run id (SignalR group, logging, todo, dedup).
        // chatId = stable per-conversation id (workspace files, editor undo, bash).
        var taskId = job.TaskId;
        var chatId = job.ChatId;

        var group = _hubContext.Clients.Group($"task_{taskId}");
        await group.SendAsync("OnLog", "[Agent Initialized] Processing user request...", ct);

        // CONVERSATION_SERVICE: history, the memory block and the resumed-answer prefix are all
        // assembled by the shared service now — this path no longer has its own copy of that logic,
        // which is what twice drifted away from the chat path.
        var messages = await _conversation.BuildRequestAsync(context, ct);

        var lastCommandFailed = false;
        var failedBuildAttempts = 0;
        var changedFiles = new HashSet<string>(StringComparer.Ordinal);
        var executedCommands = new List<string>();
        // AGENT_TERMINATION: добавлено 2026-09-23 — у обоих «не дай агенту закончить» гейтов теперь
        // есть бюджет. Раньше оба были НЕОГРАНИЧЕННЫМИ, и это и был баг «ответ написан, а генерация
        // не завершается»:
        //   * флаг lastCommandFailed «липкий» — он выставляется только при terminal_exec и больше
        //     нигде не сбрасывается, поэтому падение ЛЮБОЙ команды (в т.ч. curl/find, к сборке не
        //     относящейся) заставляло цикл требовать ещё и ещё ход каждый раз, когда модель уже
        //     пыталась завершить ответ без вызова инструментов;
        //   * гейт аудитора (`changedFiles.Count > 0`) тоже липкий — он срабатывал на КАЖДОМ
        //     последующем финальном ходе, а каждый вызов критика это отдельный большой запрос к модели.
        // С MaxIterations = 500 такой цикл выглядел как «навсегда генерирует», хотя ответ уже был готов.
        var buildGateNudges = 0;
        var auditReworks = 0;

        // AGENT_TOOL_FAILURES: добавлено 2026-09-23 — бюджет ошибок ЛЮБОГО инструмента (раньше он был
        // только у bash-команд), чтобы инструмент, который в этом окружении не может сработать в
        // принципе (take_screenshot без Chromium), не мог бесконечно продолжать прогон.
        // Считаем именно СЕРИЮ неудач подряд, а не общее число: счётчик сбрасывается на любом успешном
        // вызове, поэтому обычная работа (тест упал → поправил → тест прошёл) его никогда не наберёт,
        // а чистая петля на недоступном инструменте — наберёт за считанные шаги.
        var consecutiveToolFailures = 0;
        var totalToolFailures = 0;
        var toolFailureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nudgedTools = new HashSet<string>(StringComparer.Ordinal);
        var toolBudgetExhausted = false;
        var completedSteps = 0;

        // Инструменты этого прогона: take_screenshot убирается, если Chromium в окружении нет.
        _tools = await ResolveToolsAsync(ct);

        for (var step = 1; step <= _maxIterations; step++)
        {
            completedSteps = step;
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

            // TASK_COMPLETION_DIAGNOSTICS: добавлено 2026-09-22 — по этой строке видно, крутится ли
            // агентский цикл после того, как ответ уже выглядит законченным (главная гипотеза
            // «генерация не завершается»): растущий номер шага при нулевом тексте = петля,
            // а один шаг с toolCalls=0 = нормальное завершение.
            _logger.LogInformation(
                "Agent iteration " + step + "/" + _maxIterations +
                " task=" + taskId +
                " toolCalls=" + (responseMessage.ToolCalls?.Count ?? 0) +
                " contentChars=" + (responseMessage.Text?.Length ?? 0) +
                " historyAttached=" + Math.Max(0, messages.Count - 3));

            if (responseMessage.ToolCalls == null || responseMessage.ToolCalls.Count == 0)
            {
                // The worker may not finalize while a build/test is still red — but only for a
                // bounded number of nudges. After that the model's explicit final answer wins,
                // because nothing guarantees another round will change its mind, and an unbounded
                // gate is indistinguishable from a hang for the user.
                if (lastCommandFailed && buildGateNudges < MaxSelfRepairAttempts)
                {
                    buildGateNudges++;
                    _logger.LogInformation(
                        "Agent build gate: last command failed, forcing rework {Attempt}/{Max} task={TaskId}",
                        buildGateNudges, MaxSelfRepairAttempts, taskId);
                    messages.Add(new ChatMessage("system", CorrectionPrompt));
                    continue;
                }

                if (lastCommandFailed)
                {
                    _logger.LogWarning(
                        "Agent build gate: still red after {Max} nudges; honouring the model's final answer. task={TaskId}",
                        MaxSelfRepairAttempts, taskId);
                }

                var finalText = ExtractTextContent(responseMessage);

                // Maker-Checker only applies to real code changes. A pure dialog reply
                // must be returned verbatim instead of being swallowed by the auditor.
                if (_profile.AuditsCode && changedFiles.Count > 0 && auditReworks < MaxSelfRepairAttempts)
                {
                    // AGENT_TERMINATION: аудитор — это отдельный большой запрос к модели, который
                    // идёт ПОСЛЕ того, как ответ уже улетел в чат токенами. Без этой строки пауза
                    // выглядела как «ответ закончился, а генерация всё ещё идёт» — то есть ровно
                    // как зависание. Стадия именно `thinking`, чтобы фронт открыл видимый шаг в
                    // ленте (другие стадии лента только закрывают и ничего не показывают).
                    await SendAgentStatusAsync(taskId, "thinking", "Проверяю изменения (аудитор)…", ct: ct);
                    await group.SendAsync("OnLog", "[Auditor] Reviewing the workspace changes…", ct);

                    var audit = await ReviewWorkspaceAsync(chatId, job.Prompt, changedFiles, ct);
                    if (!audit.Approved)
                    {
                        auditReworks++;
                        _logger.LogInformation(
                            "Agent auditor: REJECT {Attempt}/{Max} with {Issues} issue(s); sending back for rework. task={TaskId}",
                            auditReworks, MaxSelfRepairAttempts, audit.Issues.Count, taskId);
                        await group.SendAsync("OnLog", $"[Auditor] REJECT — {audit.Issues.Count} issue(s) found. Sending back for rework.", ct);
                        messages.Add(new ChatMessage("system",
                            "[Auditor REJECT] The reviewer found the following problems. Fix every item, then re-run verification:\n- " +
                            string.Join("\n- ", audit.Issues)));
                        continue;
                    }

                    await group.SendAsync(
                        "OnLog",
                        audit.SkipReason is null
                            ? "[Auditor] APPROVED — applying changes."
                            : $"[Auditor] Skipped ({audit.SkipReason}) — finalizing without review.",
                        ct);
                }
                else if (_profile.AuditsCode && changedFiles.Count > 0)
                {
                    _logger.LogWarning(
                        "Agent auditor: rework budget exhausted; finalizing without re-review. task={TaskId}",
                        taskId);
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

                // AGENT_TERMINATION: без этой строки нельзя было отличить «модель закончила» от
                // «гейт отправил на доработку» — теперь виден и шаг, и число доработок.
                _logger.LogInformation(
                    "Agent loop finished at step {Step}/{Max} task={TaskId} nudges={Nudges} auditReworks={Reworks} changedFiles={Files} textChars={Chars}",
                    step, _maxIterations, taskId, buildGateNudges, auditReworks, changedFiles.Count, finalText.Length);

                // No file modifications were made: the model's text is the whole answer.
                if (changedFiles.Count == 0)
                {
                    return answer;
                }

                // File changes were made: return the final report plus a summary card.
                return BuildFinalReport(finalText, changedFiles, executedCommands);
            }

            var failedThisIteration = false;
            // AGENT_TOOL_FAILURES: инструменты, упавшие именно в этом ходе (для одного сообщения
            // с подсказкой после всего батча tool-результатов).
            var failedToolsThisIteration = new HashSet<string>(StringComparer.Ordinal);

            foreach (var toolCall in responseMessage.ToolCalls)
            {
                // COWORK_MODE: a tool outside the mode's profile is never executed, and a refused
                // call must not show up in the "Commands executed" summary either.
                var toolAllowed = _profile.Allows(toolCall.Function.Name);
                if (toolAllowed)
                {
                    RecordToolEffect(toolCall, changedFiles, executedCommands);
                }

                await group.SendAsync("OnLog", $"[Tool Call] Executing {toolCall.Function.Name}...", ct);

                ConexyToolResult result;
                // AGENT_TOOL_FAILURES: ЕДИНАЯ точка вызова инструмента. Раньше take_screenshot шёл в
                // обход DispatchToolAsync (и его catch), поэтому исключение из HandleScreenshotAsync
                // улетало из RunLoopAsync в воркер: задача падала в Failed на полуслове, вместо того
                // чтобы отдать модели обычную ошибку инструмента. Теперь через этот try/catch идёт
                // ЛЮБОЙ инструмент, и ни один из них не может прервать прогон исключением.
                try
                {
                    result = !toolAllowed
                        ? new ConexyToolResult(
                            toolCall.Id,
                            $"Tool '{toolCall.Function.Name}' is not available in {_profile.Mode} mode.",
                            true)
                        : toolCall.Function.Name == "take_screenshot"
                            ? await HandleScreenshotAsync(taskId, toolCall, messages, group, ct)
                            : await DispatchToolAsync(taskId, chatId, toolCall, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Остановка пользователем/хостом обязана завершить прогон, а не стать tool-ошибкой.
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Agent tool {Tool} threw; reporting it as a normal tool error. task={TaskId}",
                        toolCall.Function.Name, taskId);
                    result = new ConexyToolResult(
                        toolCall.Id,
                        $"Tool error ({toolCall.Function.Name}): {ex.Message}",
                        true);
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

                // AGENT_TOOL_FAILURES: счёт ошибок идёт по ЛЮБОМУ инструменту, а не только по bash.
                // Отклонённая пользователем команда ошибкой не считается (USER_REJECTED приходит с
                // IsError = false) — это решение пользователя, а не сломанное окружение.
                if (result.IsError)
                {
                    totalToolFailures++;
                    consecutiveToolFailures++;
                    toolFailureCounts[toolCall.Function.Name] =
                        toolFailureCounts.GetValueOrDefault(toolCall.Function.Name) + 1;
                    failedToolsThisIteration.Add(toolCall.Function.Name);
                }
                else
                {
                    // Прогресс есть — серия неудач прервана.
                    consecutiveToolFailures = 0;
                }
            }

            if (failedThisIteration)
            {
                messages.Add(new ChatMessage("system", CorrectionPrompt));
            }

            // AGENT_TOOL_FAILURES: один явный сигнал на инструмент, который в этом окружении не
            // работает, — чтобы модель перестала его вызывать и закончила ответ без него.
            // Ставится ПОСЛЕ всего батча tool-результатов, чтобы не разрывать пары
            // assistant(tool_calls) → tool(...), которые требует формат запроса к модели.
            var newlyUnavailable = failedToolsThisIteration
                .Where(name =>
                    toolFailureCounts.GetValueOrDefault(name) >= ToolRetryWarningLimit &&
                    nudgedTools.Add(name))
                .ToList();
            if (newlyUnavailable.Count > 0)
            {
                messages.Add(new ChatMessage("system", ToolUnavailablePrompt(newlyUnavailable)));
            }

            // AGENT_TOOL_FAILURES: и жёсткая верхняя граница. Дальше модели не даётся шанса крутить
            // неудачные вызовы: цикл завершается с тем текстом, который уже написан.
            if (consecutiveToolFailures >= ToolFailureBudget)
            {
                toolBudgetExhausted = true;
                _logger.LogWarning(
                    "Agent tool failure budget exhausted ({Failures} failures in a row, {Total} total); ending the run with the text produced so far. task={TaskId} failedTools=[{Tools}]",
                    consecutiveToolFailures, totalToolFailures, taskId, string.Join(", ", nudgedTools));
                await group.SendAsync(
                    "OnLog",
                    $"[Tool Errors] {consecutiveToolFailures} failed tool call(s) in a row — finishing with the work already done.",
                    ct);
                break;
            }
        }

        _logger.LogWarning(
            "Agent loop stopped early: reason={Reason} step={Step}/{Max} consecutiveToolFailures={Consecutive} totalToolFailures={Total} nudges={Nudges} auditReworks={Reworks} changedFiles={Files}",
            toolBudgetExhausted ? "tool-failure-budget" : "iteration-cap",
            completedSteps, _maxIterations, consecutiveToolFailures, totalToolFailures, buildGateNudges, auditReworks, changedFiles.Count);

        // Исчерпанный бюджет ошибок инструментов — это НЕ ошибка сборки: задача не завершена по
        // причине окружения, и ронять прогон в Failed здесь неправильно. Отдаём то, что модель уже
        // написала, чтобы в чат ушёл реальный ответ (и он же попал в историю), а не служебная строка.
        if (lastCommandFailed && !toolBudgetExhausted)
        {
            throw new InvalidOperationException("Agent could not resolve build/execution errors within the iteration limit.");
        }

        var accumulated = _streamedOutput.ToString().Trim();
        if (accumulated.Length > 0)
        {
            return accumulated;
        }

        return toolBudgetExhausted
            ? "Не удалось завершить задачу: инструменты, которые нужны агенту, не работают в этом окружении. Запустите задачу заново после устранения ограничения."
            : "Task reached maximum autonomous iteration limit.";
    }

    /// <summary>
    /// Tool set for the current run. <c>take_screenshot</c> is advertised only when headless Chromium
    /// can actually be launched: otherwise the model would keep calling a tool that is guaranteed to
    /// fail (it did exactly that for SVG/UI work, where a visual check looks like a required
    /// verification step), burning iterations and derailing the run.
    /// </summary>
    private async Task<List<object>> ResolveToolsAsync(CancellationToken ct)
    {
        if (_profile.AllowedTools is not null)
        {
            // Cowork never renders pages, so there is nothing to probe.
            return CoworkTools;
        }

        if (await _visionService.IsAvailableAsync(ct))
        {
            return AvailableTools;
        }

        _logger.LogWarning(
            "Agent tool set: take_screenshot excluded — headless Chromium is unavailable. task={TaskId}",
            _job.TaskId);
        return ToolsWithoutScreenshot;
    }

    /// <summary>
    /// One-time instruction for tools that failed in this environment, so the model stops retrying
    /// them and finishes the task without them.
    /// </summary>
    private static string ToolUnavailablePrompt(IReadOnlyCollection<string> tools) =>
        "[Tool Unavailable] These tools failed and will keep failing in this environment: " +
        string.Join(", ", tools) +
        ". Do NOT call them again. Continue with the remaining tools; if the failed step was optional " +
        "(for example a visual verification), skip it and state that limitation explicitly in your final answer.";

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

                // OFFICE_FORMATS: добавлено 2026-09-23
                case "create_document":
                {
                    var path = GetString(root, "path");
                    if (string.IsNullOrWhiteSpace(path) || !OfficeDocumentWriter.TryParseFormat(Path.GetExtension(path), out var format))
                        return new ConexyToolResult(toolCall.Id, "create_document requires 'path' ending in .docx, .xlsx or .pptx.", true);
                    var content = GetString(root, "content");
                    if (string.IsNullOrWhiteSpace(content))
                        return new ConexyToolResult(toolCall.Id, "create_document requires non-empty 'content' (Markdown).", true);

                    await SendAgentStatusAsync(taskId, "writing", $"Создаю документ {Path.GetFileName(path)}...", file: path, ct: ct);
                    var bytes = OfficeDocumentWriter.Create(format, content, Optional(root, "title") ?? Path.GetFileNameWithoutExtension(path));
                    var res = await _workspaceService.WriteBytesAsync(chatId, path, bytes, ct);
                    if (res.Success)
                    {
                        await SendFileCreatedAsync(taskId, path, ct);
                    }
                    await LogAsync(taskId, $"[Document Created] {path}", ct);
                    return new ConexyToolResult(toolCall.Id, res.Success ? $"Document '{path}' created ({bytes.Length} bytes)." : res.Error!, !res.Success);
                }

                case "read_document_file":
                {
                    var path = GetString(root, "path");
                    if (string.IsNullOrWhiteSpace(path))
                        return new ConexyToolResult(toolCall.Id, "read_document_file requires 'path'.", true);
                    if (!DocumentParser.CanExtract(path))
                        return new ConexyToolResult(toolCall.Id, $"Text cannot be extracted from '{Path.GetFileName(path)}'. Supported: .docx, .xlsx, .pptx, .pdf and text files.", true);

                    var res = await _workspaceService.ReadBytesAsync(chatId, path, ct);
                    if (!res.Success)
                        return new ConexyToolResult(toolCall.Id, res.Error!, true);

                    var text = DocumentParser.Parse(res.Content!, path).Trim();
                    return new ConexyToolResult(
                        toolCall.Id,
                        text.Length == 0 ? $"No text found in '{path}' (it may be a scan or an empty document)." : Truncate(text, 60_000),
                        false);
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
                    // CONFIRM_GATE: добавлено 2026-09-22 — legacy-инструмент больше не является
                    // обходом подтверждения. Схема инструментов его не предлагает, но если модель
                    // всё же его вызовет, команда идёт через тот же гейт, что и `bash`.
                    var command = GetString(root, "command");
                    if (string.IsNullOrEmpty(command))
                        return new ConexyToolResult(toolCall.Id, "terminal_exec requires 'command'.", true);

                    var legacyRequest = new BashToolRequest
                    {
                        Command = command,
                        TimeoutSeconds = null,
                        IsDangerous = _dangerousCommandClassifier.IsDangerous(command)
                    };

                    return await RunBashWithConfirmationAsync(
                        taskId, chatId, toolCall, legacyRequest, legacyRequest.IsDangerous, ct);
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
                    // TOOL_PILLS: добавлено 2026-09-20 — web_search does not go through the
                    // bash/editor services, so emit its own live-action pill.
                    await SendToolActionAsync(taskId, "web_search", request.Query, "started", $"Ищу в интернете: {request.Query}...", null, ct);

                    var res = await _webSearchService.SearchAsync(request.Query, ct);
                    await SendToolActionAsync(
                        taskId,
                        "web_search",
                        request.Query,
                        res.Success ? "completed" : "failed",
                        res.Success ? "Поиск завершён" : res.Error,
                        res.Success ? res.Output : res.Error,
                        ct);
                    return new ConexyToolResult(toolCall.Id, res.Output, !res.Success);
                }

                // RAG: добавлено 2026-09-17
                case "search_documents":
                {
                    var request = JsonSerializer.Deserialize<SearchDocumentsRequest>(toolCall.Function.Arguments);
                    if (request is null || string.IsNullOrWhiteSpace(request.Query))
                        return new ConexyToolResult(toolCall.Id, "search_documents requires 'query'.", true);

                    await SendAgentStatusAsync(taskId, "searching", $"Ищу в документах: {request.Query}...", ct: ct);
                    // TOOL_PILLS: добавлено 2026-09-20 — the pill body carries the found snippet.
                    await SendToolActionAsync(taskId, "search_documents", request.Query, "started", $"Ищу в документах: {request.Query}...", null, ct);

                    var json = await _documentService.SearchJsonAsync(_job.UserId, request.Query, request.Limit ?? 5, request.DocumentName, ct);
                    await SendToolActionAsync(taskId, "search_documents", request.Query, "completed", "Поиск завершён", json, ct);
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
        catch (CommandConfirmationException)
        {
            // CONFIRM_GATE: добавлено 2026-09-22 — сломанный гейт подтверждения обязан остановить
            // прогон. Раньше это исключение попадало в общий catch ниже, агент получал "Execution
            // error" и спокойно шёл дальше, а карточка подтверждения оставалась висеть вечно.
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // AGENT_TOOL_FAILURES: остановка пользователем — это не ошибка инструмента. Уводим её в
            // общий catch ниже, и агент вместо завершения делал бы ещё один ход с "Execution error".
            throw;
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
    ///
    /// CONFIRM_GATE: добавлено 2026-09-22 — гейт теперь fail-closed. Карточка всегда получает
    /// терминальное событие (completed/failed/rejected), а при невозможности самого запроса
    /// подтверждения команда НЕ выполняется и прогон останавливается — вместо тихого продолжения.
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

        // COMMAND_APPROVAL: добавлено 2026-09-22 — read-only диагностика (ls, cat, grep, git status,
        // find, «--version»…) больше не требует подтверждения и не открывает полноразмерную
        // карточку. Раньше гейт стоял на КАЖДОЙ команде, из-за чего лента превращалась в столбик
        // одинаковых карточек, а текст рассуждений терялся между ними.
        if (!_commandApproval.RequiresApproval(request.Command))
        {
            await SendAgentStatusAsync(taskId, "executing", $"Выполняю команду: {request.Command}...", ct: ct);
            var readOnly = await _bashService.ExecuteAsync(chatId, request, emitStartEvent: true, ct: ct);
            var readOnlyOutput = string.IsNullOrEmpty(readOnly.Output)
                ? readOnly.ErrorType ?? "command failed"
                : readOnly.Output;
            return new ConexyToolResult(toolCall.Id, readOnlyOutput, !readOnly.Success);
        }

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

        // The card is driven purely by ToolAction events, so every exit path below must emit a
        // terminal status for this action id — otherwise the buttons stay on screen forever.
        async Task EmitAsync(string status, string summary, string? output = null, CancellationToken token = default)
        {
            await _hubContext.Clients.Group($"task_{taskId}").SendAsync("ToolAction", new ToolActionEvent
            {
                ToolName = "bash",
                Command = request.Command,
                Path = "",
                Status = status,
                Summary = summary,
                WorkingDirectory = workingDirectory,
                Output = output,
                PendingActionId = actionId,
                IsDangerous = isDangerous
            }, token);
        }

        // Best-effort variant: used on failure paths, where the original error matters more than
        // whether the UI managed to clear the card.
        async Task TryEmitAsync(string status, string summary, string? output = null)
        {
            try
            {
                await EmitAsync(status, summary, output, CancellationToken.None);
            }
            catch (Exception ex)
            {
                await LogAsync(taskId, $"[Confirm] Не удалось обновить карточку команды: {ex.Message}", ct);
            }
        }

        await SendAgentStatusAsync(taskId, "confirming", $"Ожидает подтверждения: {request.Command}", ct: ct);
        await EmitAsync("pending_confirmation", $"Ожидает подтверждения: {request.Command}", token: ct);

        bool approved;
        try
        {
            approved = await _pendingActionService.WaitForDecisionAsync(
                actionId, _job.UserId, chatId, taskId, request.Command, workingDirectory, isDangerous, ct);
        }
        catch (CommandConfirmationException)
        {
            // The gate itself failed: nothing was approved and nothing may run. Clear the card
            // and stop the run with a visible error rather than pretending the tool failed.
            await TryEmitAsync("failed", "Не удалось запросить подтверждение — команда не выполнена.");
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryEmitAsync("rejected", "Подтверждение отменено (таймаут или остановка).");
            return new ConexyToolResult(
                toolCall.Id,
                $"CANCELLED: подтверждение команды '{request.Command}' не получено (таймаут или остановка). Команда НЕ выполнена.",
                true);
        }

        if (!approved)
        {
            await EmitAsync("rejected", "Отклонено пользователем", token: ct);

            return new ConexyToolResult(
                toolCall.Id,
                $"USER_REJECTED: команда '{request.Command}' отклонена пользователем и НЕ выполнена. Предложи альтернативный способ или продолжи без неё.",
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
            .StreamChatAsync(messages, _job.ModelType, _job.ReasoningEffort, _tools, taskId: _job.TaskId, ct: ct)
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
                        _job.ModelType, messages, _tools, _job.ReasoningEffort, _job.TaskId, ct);
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
                    // PARTIAL_TURN_PERSIST: keeps the worker able to persist a stopped turn.
                    _streamedOutput.Append(delta.Content);
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

    // TOOL_PILLS: добавлено 2026-09-20
    /// <summary>
    /// Streams a live-action event for a tool that does not run through the bash/editor
    /// services, so the chat can render an execution pill (running -> expandable result) for
    /// it too. The payload is truncated: it is for display only, the model still gets the
    /// full result through the tool result.
    /// </summary>
    private async Task SendToolActionAsync(
        Guid taskId,
        string toolName,
        string command,
        string status,
        string? summary,
        string? output,
        CancellationToken ct)
    {
        await _hubContext.Clients.Group($"task_{taskId}").SendAsync("ToolAction", new ToolActionEvent
        {
            ToolName = toolName,
            Command = command,
            Path = "",
            Status = status,
            Summary = summary,
            Output = TruncateToolOutput(output)
        }, ct);
    }

    private const int MaxToolOutputChars = 4000;

    private static string? TruncateToolOutput(string? output) =>
        string.IsNullOrEmpty(output) || output.Length <= MaxToolOutputChars
            ? output
            : output[..MaxToolOutputChars] + "\n…";

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

    // CONVERSATION_SERVICE: добавлено 2026-09-23
    /// <summary>
    /// Maker-Checker auditor. <b>Deliberately does NOT use <see cref="IConversationService"/></b>:
    /// it is not a conversation turn but an internal review of the workspace, so it has its own
    /// fixed system prompt (<see cref="CriticSystemPrompt"/>) and its own payload (file contents),
    /// and it must neither see nor pollute the user-visible dialog history. Its tokens still count
    /// toward the agent budget.
    /// <para>
    /// If you are looking at this because history handling "looks missing" here — it is not
    /// missing, it is intentional. Do not route it through the conversation service.
    /// </para>
    /// <para>
    /// AUDITOR_BUDGET: добавлено 2026-09-23. The audit is post-processing of an answer the user has
    /// already seen, so it is bounded as a whole by <see cref="_auditTimeout"/> and it never fails
    /// the task: a timeout or an upstream error finalizes the answer without review (the same
    /// fail-open rule <see cref="ParseAuditVerdict"/> already applies to a malformed verdict).
    /// Only a stop by the user or the host still propagates.
    /// </para>
    /// </summary>
    private async Task<AuditVerdict> ReviewWorkspaceAsync(
        Guid chatId,
        string taskPrompt,
        IReadOnlyCollection<string> changedFiles,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var auditCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        auditCts.CancelAfter(_auditTimeout);

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("## Task");
            builder.AppendLine(taskPrompt);
            builder.AppendLine();
            builder.AppendLine("## Workspace changes");

            var reviewedFiles = 0;
            foreach (var file in changedFiles.Take(AuditMaxFiles))
            {
                // OFFICE_FORMATS: .docx/.xlsx/.pptx are zip files — read as text they are only noise.
                if (OfficeDocumentWriter.TryParseFormat(Path.GetExtension(file), out _))
                {
                    continue;
                }

                var read = await _workspaceService.ReadFileAsync(chatId, file, auditCts.Token);
                if (!read.Success || string.IsNullOrWhiteSpace(read.Content))
                {
                    // Deleted or emptied since it was written — nothing to review.
                    continue;
                }

                var snippet = Truncate(read.Content, AuditMaxCharsPerFile);
                if (reviewedFiles > 0 && builder.Length + snippet.Length > AuditMaxPromptChars)
                {
                    break;
                }

                builder.AppendLine($"### {file}");
                builder.AppendLine(snippet);
                reviewedFiles++;
            }

            if (reviewedFiles == 0)
            {
                return AuditVerdict.ApprovedVerdict;
            }

            _logger.LogInformation(
                "Agent auditor: started task={TaskId} files={Reviewed}/{Changed} promptChars={Chars} timeout={Timeout}s",
                _job.TaskId, reviewedFiles, changedFiles.Count, builder.Length, _auditTimeout.TotalSeconds);

            var messages = new List<ChatMessage>
            {
                new("system", CriticSystemPrompt),
                new("user", builder.ToString())
            };

            var response = await _llmClient.SendChatAsync(ConexyModelType.ConexyV1Pro, messages, new List<object>(), _job.ReasoningEffort, _job.TaskId, auditCts.Token);
            // SUBSCRIPTION_TIERS: добавлено 2026-09-17 — critic tokens count toward the agent budget.
            await _subscriptionService.RecordAgentTokensAsync(_job.UserId, response.TotalTokens, ct);
            var verdict = ParseAuditVerdict(response.Message.Text ?? string.Empty);

            _logger.LogInformation(
                "Agent auditor: verdict={Verdict} issues={Issues} elapsedMs={Elapsed} task={TaskId}",
                verdict.Approved ? "APPROVED" : "REJECT", verdict.Issues.Count, stopwatch.ElapsedMilliseconds, _job.TaskId);
            return verdict;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop by the user or the host must still end the run.
            throw;
        }
        catch (Exception ex)
        {
            var reason = auditCts.IsCancellationRequested
                ? $"no verdict within {_auditTimeout.TotalSeconds:0}s"
                : "reviewer unavailable";
            _logger.LogWarning(
                ex,
                "Agent auditor: {Reason} after {Elapsed}ms; finalizing the delivered answer without review. task={TaskId}",
                reason, stopwatch.ElapsedMilliseconds, _job.TaskId);
            return AuditVerdict.Skipped(reason);
        }
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
                case "create_document":
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
        catch (Exception)
        {
            // Best-effort bookkeeping; DispatchToolAsync validates arguments authoritatively.
            // AGENT_TOOL_FAILURES: расширено с JsonException до Exception — model-supplied arguments
            // могут быть валидным JSON, но не объектом (например "[]"), и тогда TryGetProperty ниже
            // бросает InvalidOperationException. Эта функция вызывается вне try/catch вызова
            // инструмента, поэтому такое исключение роняло весь прогон агента.
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

    private record AuditVerdict(bool Approved, IReadOnlyList<string> Issues, string? SkipReason = null)
    {
        public static AuditVerdict ApprovedVerdict { get; } = new(true, Array.Empty<string>());

        // AUDITOR_BUDGET: the review did not happen (timeout / upstream error); the answer stands.
        public static AuditVerdict Skipped(string reason) => new(true, Array.Empty<string>(), reason);
    }

    private static List<object> BuildToolSchemas(bool includeScreenshot)
    {
        var tools = new List<object>
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
        // OFFICE_FORMATS: добавлено 2026-09-23
        Function("create_document", "Create a Word (.docx), Excel (.xlsx) or PowerPoint (.pptx) file in the workspace from Markdown; the format comes from the path extension. .docx: headings, paragraphs, bullet/numbered lists, tables and **bold**. .xlsx: every Markdown table becomes a sheet named after the heading above it (numbers are stored as numbers); plain CSV text also works. .pptx: every '#'/'##' heading starts a slide and the lines under it become its body (overlong slides continue automatically).",
            new { type = "object", properties = new {
                path = new { type = "string", description = "Relative path ending in .docx, .xlsx or .pptx" },
                content = new { type = "string", description = "Document content in Markdown" },
                title = new { type = "string", description = "Optional title, used when the content has no heading" }
            }, required = new[] { "path", "content" } }),
        Function("read_document_file", "Read the text of a document in the workspace: .docx, .xlsx (every sheet as a table), .pptx (every slide), .pdf or any text file. Use it for files the user attached or uploaded; str_replace_editor 'view' shows these formats as binary.",
            new { type = "object", properties = new {
                path = new { type = "string", description = "Relative path to the file" }
            }, required = new[] { "path" } }),
        };

        if (includeScreenshot)
        {
            tools.Add(Function("take_screenshot", "Render a URL or local HTML file with headless Chromium and attach the screenshot for visual audit.",
                new { type = "object", properties = new {
                    url = new { type = "string", description = "http(s) URL or local file path to render" },
                    viewport_width = new { type = "integer", description = "Viewport width in pixels (default 1280)" },
                    viewport_height = new { type = "integer", description = "Viewport height in pixels (default 800)" }
                }, required = new[] { "url" } }));
        }

        return tools;
    }

    // COWORK_MODE: the schemas are anonymous objects; the name is read back from their JSON shape
    // ({ type, function: { name, ... } }) once, at type initialization.
    private static string ToolName(object schema) =>
        JsonSerializer.SerializeToElement(schema).GetProperty("function").GetProperty("name").GetString() ?? string.Empty;

    private static object Function(string name, string description, object parameters) => new
    {
        type = "function",
        function = new { name, description, parameters }
    };
}

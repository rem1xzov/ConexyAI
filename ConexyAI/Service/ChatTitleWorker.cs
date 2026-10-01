using ConexyAI.Contract;
using ConexyAI.Model;

namespace ConexyAI.Service;

// CHAT_TITLE_TOPIC: добавлено 2026-10-01.
/// <summary>
/// Придумывает короткое НАЗВАНИЕ ЧАТА ПО ТЕМЕ после первого хода пользователя. Работает на дешёвой
/// модели flash, в фоне, и никогда не блокирует ответ. Внутренние вызовы LLM не расходуют лимиты
/// пользователя. Имя пользователя, выбранное вручную, не перетирается.
/// </summary>
public class ChatTitleWorker : BackgroundService
{
    private readonly IChatTitleQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatTitleWorker> _logger;

    public ChatTitleWorker(
        IChatTitleQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ChatTitleWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Сколько символов первого сообщения отдаём модели — этого хватает, чтобы понять тему.</summary>
    private const int MaxSourceChars = 2000;

    /// <summary>Верхняя граница длины готового названия (колонка — 200 символов).</summary>
    private const int MaxTitleChars = 80;

    private const string TitleSystemPrompt =
        """
        Ты придумываешь короткое название для чата по первой просьбе пользователя.
        Верни ТОЛЬКО название — 2–5 слов, называющих ТЕМУ разговора, а не пересказ вопроса.
        Пиши на языке пользователя. Без кавычек, без точки в конце, без вводных слов («помоги», «сделай», «напиши», «вопрос», «задача»).
        Примеры: «Игра Тетрис», «Настройка Nginx и HTTPS», «Отчёт по продажам за квартал», «Разбор ошибки сборки».
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat title generation failed for user {UserId} / chat {ChatId}.", job.UserId, job.ChatId);
            }
        }
    }

    private async Task ProcessAsync(ChatTitleJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var conversation = scope.ServiceProvider.GetRequiredService<IConversationService>();

        // Удалённый чат название не получает (иначе генерация воскрешала бы его в списке).
        var chatAccess = scope.ServiceProvider.GetService<IChatAccessService>();
        if (chatAccess is not null && await chatAccess.IsDeletedAsync(job.ChatId, ct))
            return;

        // Имя, заданное пользователем вручную, неприкосновенно: если оно уже есть — ничего не делаем.
        var summary = await conversation.GetChatAsync(job.UserId, job.ChatId, ct);
        if (summary is null || !string.IsNullOrWhiteSpace(summary.Title))
            return;

        if (string.IsNullOrWhiteSpace(job.FirstUserMessage))
            return;

        // Берём первое сообщение пользователя и краткий первый ответ ассистента: по паре тема
        // определяется точнее, чем по одной реплике (особенно когда запрос короткий).
        var source = BuildSource(job.FirstUserMessage, summary.LastAssistantMessage);

        var llmClient = scope.ServiceProvider.GetRequiredService<IConexyLlmClient>();
        var messages = new List<ChatMessage>
        {
            new("system", TitleSystemPrompt),
            new("user", source)
        };

        var result = await llmClient.SendChatAsync(
            ConexyModelType.ConexyV1Flash, messages, new List<object>(), taskId: null, ct: ct);

        var title = Sanitize(result.Message.Text);
        if (title is null)
        {
            _logger.LogWarning("Chat title for {ChatId} was empty or unusable; keeping the fallback.", job.ChatId);
            return;
        }

        // RenameChatAsync возвращает 0, если чат не принадлежит пользователю — это защита владельца.
        var updated = await conversation.RenameChatAsync(job.UserId, job.ChatId, title, ct);
        _logger.LogInformation(
            "Chat title generated for {ChatId}: '{Title}' (rows={Rows}).", job.ChatId, title, updated);
    }

    /// <summary>Первое сообщение пользователя плюс сжатый первый ответ — вместе это тема разговора.</summary>
    private static string BuildSource(string userMessage, string? assistantMessage)
    {
        var user = Truncate(userMessage, MaxSourceChars);
        if (string.IsNullOrWhiteSpace(assistantMessage))
            return user;

        return $"Пользователь: {user}\nАссистент (начало ответа): {Truncate(assistantMessage, AssistantSnippetChars)}";
    }

    private static string Truncate(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    /// <summary>Сколько символов первого ответа ассистента берём для контекста темы.</summary>
    private const int AssistantSnippetChars = 800;

    /// <summary>
    /// Приводит ответ модели к годному названию: одна строка, без окружающих кавычек и завершающей
    /// точки, с обрезанием по границе слова. Возвращает <c>null</c>, если годного названия нет.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();

        // Модель иногда отвечает «Название: …» — отбрасываем префикс.
        var colon = text.IndexOf(':');
        if (colon is > 0 and <= 15)
        {
            var prefix = text[..colon].Replace(" ", string.Empty).ToLowerInvariant();
            if (prefix is "название" or "title" or "тема")
                text = text[(colon + 1)..].Trim();
        }

        // Только первая строка и без обрамляющих кавычек/бэктиков/ёлочек.
        text = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        text = text.Trim('"', '\'', '`', '«', '»', '“', '”').Trim();

        // Схлопываем любые пробелы (включая табы) в один.
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (text.EndsWith('.'))
            text = text[..^1].TrimEnd();

        if (text.Length == 0)
            return null;

        if (text.Length > MaxTitleChars)
        {
            var cut = text[..MaxTitleChars];
            var lastSpace = cut.LastIndexOf(' ');
            text = (lastSpace > 20 ? cut[..lastSpace] : cut).TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }
}

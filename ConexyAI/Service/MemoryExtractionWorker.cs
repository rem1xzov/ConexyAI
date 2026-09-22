using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Background worker that summarizes a user's durable memory facts. Runs on a bounded
/// channel, uses the cheaper <c>ConexyV1-flash</c> model, and never blocks the user's
/// answer. Internal LLM calls do NOT count against the user's subscription limits.
/// </summary>
public class MemoryExtractionWorker : BackgroundService
{
    private readonly IMemoryExtractionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<MemoryExtractionWorker> _logger;

    private const string ExtractionPrompt =
        """
        Ты — система извлечения долговременных фактов о пользователе.
        Извлекай только устойчивые факты: имя, профессия, проекты, долгосрочные предпочтения, технологии, значимые жизненные обстоятельства.
        Игнорируй сиюминутный контекст текущей задачи/бага и случайные разговоры.

        Сначала — текущий список фактов пользователя (учитывай его, обновляй и исправляй, не дублируй):

        {facts}

        Последние сообщения диалога:

        {dialog}

        Верни ОБНОВЛЁННЫЙ полный список фактов строго как JSON-массив строк (без markdown, без пояснений).
        Добавляй новые факты, удаляй устаревшие и противоречащие. Пример: ["Работает в X", "Предпочитает Python"].
        Если устойчивых фактов нет — верни пустой массив [].
        """;

    public MemoryExtractionWorker(
        IMemoryExtractionQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<MemoryOptions> options,
        ILogger<MemoryExtractionWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

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
                _logger.LogError(ex, "Memory extraction failed for user {UserId} / chat {ChatId}.", job.UserId, job.ChatId);
            }
        }
    }

    private async Task ProcessAsync(MemoryExtractionJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var chatHistory = scope.ServiceProvider.GetRequiredService<IChatHistoryRepository>();
        var memoryRepository = scope.ServiceProvider.GetRequiredService<IUserMemoryRepository>();
        var llmClient = scope.ServiceProvider.GetRequiredService<IConexyLlmClient>();

        var history = await chatHistory.GetMessagesAsync(job.UserId, job.ChatId, ct);
        var recent = history.TakeLast(_options.Value.RecentMessagesToReview).ToList();

        var currentFacts = await memoryRepository.GetFactsAsync(job.UserId, ct);

        var factsBlock = currentFacts.Count == 0
            ? "(пусто)"
            : string.Join("\n", currentFacts.Select(f => $"- {f.FactText}"));

        var dialogBlock = recent.Count == 0
            ? "(пусто)"
            : string.Join("\n", recent.Select(m => $"[{m.Role}]: {m.Content}"));

        var prompt = ExtractionPrompt
            .Replace("{facts}", factsBlock)
            .Replace("{dialog}", dialogBlock);

        var messages = new List<ChatMessage>
        {
            new("system", "Ты — система извлечения фактов о пользователе. Отвечай только JSON-массивом строк."),
            new("user", prompt)
        };

        var result = await llmClient.SendChatAsync(
            ConexyModelType.ConexyV1Flash, messages, new List<object>(), taskId: null, ct: ct);

        var facts = ParseFacts(result.Message.Text ?? string.Empty);
        if (facts.Count == 0)
        {
            _logger.LogInformation("Memory extraction for user {UserId} produced no facts; skipping write.", job.UserId);
            return;
        }

        await memoryRepository.ReplaceFactsAsync(job.UserId, facts, ct);
        _logger.LogInformation(
            "Memory extraction completed for user {UserId}: {Count} facts (tokens={Tokens}).",
            job.UserId, facts.Count, result.TotalTokens);
    }

    private static IReadOnlyList<string> ParseFacts(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return result;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                {
                    result.Add(el.GetString()!.Trim());
                }
            }
        }
        catch (JsonException)
        {
            // Malformed response — return empty so existing facts are left untouched.
            return result;
        }

        return result;
    }
}

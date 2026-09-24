using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;

namespace ConexyAI.Service;

/// <summary>
/// Thrown when the user exceeds the configured message quota. The controller maps
/// this specific exception to HTTP 429, keeping it distinct from EF Core and other
/// runtime <see cref="InvalidOperationException"/> failures.
/// </summary>
public class RateLimitExceededException : Exception
{
    public RateLimitExceededException(string message) : base(message) { }
}

// TURN_IN_FLIGHT: добавлено 2026-09-24 — ревью H6. Контроллер отвечает 409 вместо тихого 202.
public sealed class TurnInFlightException : Exception
{
    public TurnInFlightException(Guid taskId)
        : base($"A turn with id '{taskId}' is already queued or running.")
    {
    }
}

public class ConexyService : IConexyService
{
    private readonly IConexyRepository _repository;
    private readonly IConexyQueueGuard _queueGuard;
    private readonly IWebHostEnvironment _environment;
    // SUBSCRIPTION_TIERS: добавлено 2026-09-17
    private readonly ISubscriptionService _subscriptionService;
    // CHAT_OWNERSHIP: добавлено 2026-09-24
    private readonly IChatAccessService _chatAccess;
    private const int MaxRequestsPerWindow = 60;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);

    // INCOGNITO_CHAT: добавлено 2026-09-20 — stands in for the prompt of an incognito turn.
    internal const string IncognitoPromptPlaceholder = "(incognito)";

    public ConexyService(
        IConexyRepository repository,
        IConexyQueueGuard queueGuard,
        IWebHostEnvironment environment,
        ISubscriptionService subscriptionService,
        IChatAccessService chatAccess)
    {
        _chatAccess = chatAccess;
        _repository = repository;
        _queueGuard = queueGuard;
        _environment = environment;
        _subscriptionService = subscriptionService;
    }

    public async Task<ConexyResponse> ExecuteAsync(Guid userId, ConexyRequest request, CancellationToken ct = default)
    {
        if (!ConexyModelMapper.TryParse(request.Model, out var modelType))
        {
            throw new ArgumentException($"Invalid model: '{request.Model}'. Allowed: ConexyV1-flash, ConexyV1-pro, conexy-coder, conexy-cowork");
        }

        // SUBSCRIPTION_TIERS: добавлено 2026-09-17
        var decision = await _subscriptionService.CheckBeforeRunAsync(userId, modelType, ct);
        if (decision.Kind == UsageDecisionKind.LimitExceeded)
        {
            throw new LimitExceededException(decision.LimitName!, decision.ResetsAt ?? DateTime.UtcNow);
        }
        if (decision.Kind == UsageDecisionKind.FallbackToFlash)
        {
            modelType = ConexyModelType.ConexyV1Flash;
        }

        // Rate limiting is disabled in Development so local testing is never blocked
        // by the message quota. In other environments the rolling window still applies.
        if (!_environment.IsDevelopment())
        {
            var windowStart = DateTime.UtcNow.Subtract(WindowDuration);
            var requestsInWindow = await _repository.GetRequestCountInWindowAsync(userId, windowStart, ct);

            if (requestsInWindow >= MaxRequestsPerWindow)
            {
                throw new RateLimitExceededException("Request rate limit exceeded. Please wait a moment and try again.");
            }
        }

        // Session reuse: a non-empty SessionId keeps the same workspace (and task id)
        // across subsequent prompts in the same conversation instead of spawning a new one.
        var taskId = Guid.NewGuid();
        if (!string.IsNullOrWhiteSpace(request.SessionId) && Guid.TryParse(request.SessionId, out var requestedId))
        {
            taskId = requestedId;
        }

        // Read without tracking: this read is only for ownership validation, so keep the
        // instance detached and avoid a later attach/update with the same key hitting the
        // EF Core ChangeTracker conflict.
        var existing = await _repository.GetByIdAsNoTrackingAsync(taskId, ct);

        // Ownership mismatch: never reuse another user's session id.
        if (existing != null && existing.UserId != userId)
        {
            taskId = Guid.NewGuid();
            existing = null;
        }

        // The on-disk workspace is keyed by the chat/thread id (stable for the whole
        // conversation), not by the per-run task id. Fall back to the (own) task id for legacy
        // clients that do not send ChatId yet.
        var chatId = Guid.TryParse(request.ChatId, out var parsedChatId) ? parsedChatId : taskId;

        // CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1. Ход в чужом чате означал бы запуск агента
        // в чужом воркспейсе; первый ход в новом чате записывает владельца.
        await _chatAccess.EnsureWritableAsync(
            userId, chatId, request.Incognito, NormalizeKind(request.ChatKind), modelType.ToPublicName(), ct);

        // TURN_IN_FLIGHT: добавлено 2026-09-24 — ревью H6. Раньше второй ход с тем же id сначала
        // ПЕРЕЗАПИСЫВАЛ строку идущего хода (Pending, новый промпт, Result = null), а затем молча
        // отбрасывался дедупликацией очереди с ответом 202 — клиент ждал ответа, который никогда не
        // придёт, а поток первого хода лился во второй пузырь. Теперь id резервируется ДО любых
        // изменений строки, а занятый id — это 409.
        if (!_queueGuard.TryReserve(taskId))
        {
            throw new TurnInFlightException(taskId);
        }

        try
        {
            // INCOGNITO_CHAT: добавлено 2026-09-20
            // The task row itself has to exist — the queue, dedup guard and rate-limit counter all
            // key off it — but an incognito turn must not leave the user's text behind, so only a
            // placeholder is persisted.
            var storedPrompt = request.Incognito ? IncognitoPromptPlaceholder : request.Prompt;

            ConexyEntity entity;
            if (existing != null)
            {
                // Session reuse: update the existing row in place instead of creating a new
                // instance with the same key.
                entity = existing;
                entity.Model = modelType.ToPublicName();
                entity.Prompt = storedPrompt;
                entity.Status = ConexyStatus.Pending;
                entity.Result = null;
                entity.FinishedAt = null;
                entity.ChatId = chatId;
                await _repository.UpdateAsync(entity, ct);
            }
            else
            {
                entity = new ConexyEntity
                {
                    Id = taskId,
                    UserId = userId,
                    ChatId = chatId,
                    Model = modelType.ToPublicName(),
                    Prompt = storedPrompt,
                    Status = ConexyStatus.Pending
                };
                entity = await _repository.CreateOrGetAsync(entity, ct);
            }

            // Ставим задачу в фоновую очередь (GitHub-токен не сохраняется в БД, а передаётся только в памяти).
            await _queueGuard.EnqueueReservedAsync(new ConexyJob(entity.Id, chatId, userId, modelType, request.Prompt, request.GitHubToken, request.GitHubRepo, request.Attachments, request.Thinking, request.ReasoningEffort, request.StudentsMode, request.SmartSearch, request.Incognito, request.AssistantPrefix, request.ChatKind, request.Regenerate), ct);

            return ToResponse(entity);
        }
        catch
        {
            // Nothing was queued: free the id so the client can retry.
            _queueGuard.MarkCompleted(taskId);
            throw;
        }
    }

    // CHAT_KIND_SYNC: the same whitelist as the history writer uses.
    private static string? NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "chat" => "chat",
        "projects" => "projects",
        "students" => "students",
        _ => null,
    };

    public async Task<ConexyResponse?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var entity = await _repository.GetByIdAsNoTrackingAsync(id, ct);
        if (entity == null || entity.UserId != userId) return null;
        return ToResponse(entity);
    }

    private static ConexyResponse ToResponse(ConexyEntity e) =>
        new(e.Id, e.UserId, e.Model, e.Prompt, e.Result, e.Status.ToString(), e.CreatedAt, e.FinishedAt);
}
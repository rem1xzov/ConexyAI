using System.Collections.Concurrent;
using ConexyAI.Hub;
using ConexyAI.Service.Push;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Phone;

// PHONE_AGENT: добавлено 2026-10-29 — подтверждения действий на телефоне (инструменты C/D) и вопросы ask_user.
// Карточка в вебе — та же, что у команд ПК: событие OnPendingActionCreated (actionId = requestId), решение
// принимает существующий ConfirmAction хаба ИЛИ REST POST /api/mobile/actions/{actionId} из уведомления.

public enum PhoneApprovalOutcome
{
    Approved,
    Denied,
    TimedOut,
}

public enum PhoneDecisionResult
{
    /// <summary>Решение принято.</summary>
    Applied,

    /// <summary>Решение уже принято (или время вышло) — повтор без эффекта.</summary>
    AlreadyResolved,

    /// <summary>Такого действия нет или оно чужое.</summary>
    NotFound,
}

public interface IPhoneApprovalService
{
    /// <summary>
    /// Показывает карточку подтверждения (и push, если приложение не на экране) и ждёт решение человека.
    /// Таймаут — отказ. Автоодобрения («разрешить всё») для телефона нет.
    /// </summary>
    Task<PhoneApprovalOutcome> RequestApprovalAsync(
        Guid userId, Guid taskId, Guid chatId, string tool, string normalizedArguments, CancellationToken ct);

    /// <summary>Решение из уведомления (REST). Проверяет владельца; повторное решение — <see cref="PhoneDecisionResult.AlreadyResolved"/>.</summary>
    Task<PhoneDecisionResult> DecideAsync(Guid userId, Guid actionId, bool approved, CancellationToken ct);

    /// <summary>ask_user: показывает вопрос в вебе и ждёт ответ. Null — ответа нет (таймаут).</summary>
    Task<string?> AskQuestionAsync(Guid userId, Guid taskId, string? question, string[]? options, CancellationToken ct);

    /// <summary>Ответ из веба на вопрос агента телефона. False — вопроса нет или он чужой.</summary>
    bool AnswerQuestion(Guid userId, Guid requestId, string? answer);
}

public sealed class PhoneApprovalService : IPhoneApprovalService
{
    public static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(60);
    private const int MaxRecent = 1_000;

    private sealed record Active(Guid UserId, Guid TaskId);
    private sealed record Question(Guid UserId, Guid TaskId, TaskCompletionSource<string?> Answer);

    private readonly ConcurrentDictionary<Guid, Active> _active = new();
    private readonly ConcurrentDictionary<Guid, (Guid UserId, DateTime At)> _recent = new();
    private readonly ConcurrentDictionary<Guid, Question> _questions = new();
    // Решения, принятые из уведомления (REST): уведомление уже закрыто системой, dismiss не нужен.
    private readonly ConcurrentDictionary<Guid, byte> _decidedViaNotification = new();

    private readonly IPendingActionService _pending;
    private readonly IPhoneToolBridge _bridge;
    private readonly IPushService _push;
    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<PhoneApprovalService> _logger;
    private readonly TimeSpan _approvalTimeout;
    private readonly TimeSpan _questionTimeout;

    public PhoneApprovalService(
        IPendingActionService pending,
        IPhoneToolBridge bridge,
        IPushService push,
        IHubContext<ConexyHub> hub,
        ILogger<PhoneApprovalService> logger,
        TimeSpan? approvalTimeout = null,
        TimeSpan? questionTimeout = null)
    {
        _pending = pending;
        _bridge = bridge;
        _push = push;
        _hub = hub;
        _logger = logger;
        _approvalTimeout = approvalTimeout ?? DefaultApprovalTimeout;
        _questionTimeout = questionTimeout ?? DefaultQuestionTimeout;
    }

    public async Task<PhoneApprovalOutcome> RequestApprovalAsync(
        Guid userId, Guid taskId, Guid chatId, string tool, string normalizedArguments, CancellationToken ct)
    {
        var spec = PhoneToolCatalog.Get(tool);
        var dangerous = spec?.Class != PhoneToolClass.Confirm;
        var (title, details) = PhoneToolCatalog.Describe(tool, normalizedArguments);
        var model = _bridge.GetInfo(userId)?.Model;
        var actionId = Guid.NewGuid();

        _active[actionId] = new Active(userId, taskId);
        Task<int>? confirmPush = null;
        try
        {
            await SendStatusAsync(taskId, dangerous ? "Жду вашего подтверждения (важное действие)…" : "Жду вашего подтверждения…");

            // Приложение не на экране (или телефон офлайн) — уведомление с кнопками; иначе хватит карточки в приложении.
            if (!_bridge.IsForeground(userId))
            {
                confirmPush = Task.Run(async () =>
                {
                    try
                    {
                        return await _push.SendAsync(userId, new Dictionary<string, string>
                        {
                            ["type"] = "confirm",
                            ["actionId"] = actionId.ToString(),
                            ["taskId"] = taskId.ToString(),
                            ["chatId"] = chatId.ToString(),
                            ["title"] = title,
                            ["body"] = string.IsNullOrWhiteSpace(details) ? "Агент ConexyAI просит подтверждения." : details,
                            ["dangerous"] = dangerous ? "true" : "false",
                        }, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not send confirm push for action {ActionId}: {Error}.", actionId, ex.GetType().Name);
                        return 0;
                    }
                }, CancellationToken.None);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_approvalTimeout);
            try
            {
                var approved = await _pending.WaitForDecisionAsync(
                    actionId, userId, chatId, taskId, title,
                    string.IsNullOrWhiteSpace(model) ? "Телефон" : $"Телефон: {model}",
                    dangerous, timeoutCts.Token, details, _approvalTimeout);
                return approved ? PhoneApprovalOutcome.Approved : PhoneApprovalOutcome.Denied;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Phone approval {ActionId} timed out; treated as denied.", actionId);
                return PhoneApprovalOutcome.TimedOut;
            }
        }
        finally
        {
            _active.TryRemove(actionId, out _);
            Remember(actionId, userId);
            var viaNotification = _decidedViaNotification.TryRemove(actionId, out _);
            if (confirmPush is not null && !viaNotification)
                DismissNotification(userId, actionId, confirmPush);
        }
    }

    // Решение принято в вебе/приложении или время вышло — уведомление с кнопками больше не нужно. Шлём после
    // confirm-push (порядок важен) и только если тот реально ушёл (есть токены).
    private void DismissNotification(Guid userId, Guid actionId, Task<int> confirmPush)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (await confirmPush <= 0)
                    return;
                await _push.SendAsync(userId, new Dictionary<string, string>
                {
                    ["type"] = "dismiss",
                    ["actionId"] = actionId.ToString(),
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not send dismiss push for action {ActionId}: {Error}.", actionId, ex.GetType().Name);
            }
        }, CancellationToken.None);
    }

    public async Task<PhoneDecisionResult> DecideAsync(Guid userId, Guid actionId, bool approved, CancellationToken ct)
    {
        if (_active.TryGetValue(actionId, out var active))
        {
            if (active.UserId != userId)
                return PhoneDecisionResult.NotFound;
            // Метка ставится ДО подтверждения: ожидающий поток просыпается внутри ConfirmActionAsync.
            _decidedViaNotification[actionId] = 1;
            var applied = false;
            try
            {
                applied = await _pending.ConfirmActionAsync(actionId, userId, approved, false, ct);
            }
            finally
            {
                if (!applied)
                    _decidedViaNotification.TryRemove(actionId, out _);
            }
            return applied ? PhoneDecisionResult.Applied : PhoneDecisionResult.AlreadyResolved;
        }

        if (_recent.TryGetValue(actionId, out var recent) && recent.UserId == userId
            && DateTime.UtcNow - recent.At < RecentTtl)
            return PhoneDecisionResult.AlreadyResolved;

        return PhoneDecisionResult.NotFound;
    }

    private void Remember(Guid actionId, Guid userId)
    {
        _recent[actionId] = (userId, DateTime.UtcNow);
        if (_recent.Count <= MaxRecent)
            return;
        foreach (var (id, entry) in _recent)
        {
            if (DateTime.UtcNow - entry.At > RecentTtl)
                _recent.TryRemove(id, out _);
        }
    }

    public async Task<string?> AskQuestionAsync(Guid userId, Guid taskId, string? question, string[]? options, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[requestId] = new Question(userId, taskId, tcs);
        try
        {
            await SendStatusAsync(taskId, "Жду вашего ответа…");
            var cleanOptions = (options ?? Array.Empty<string>())
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Take(10)
                .Select(o => Limit(o, 200))
                .ToArray();
            await _hub.Clients.Group($"task_{taskId}").SendAsync("OnDeviceQuestion", new
            {
                requestId,
                taskId,
                question = Limit(question, 4_000),
                options = cleanOptions,
            }, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_questionTimeout);
            await using var registration = timeout.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task;
        }
        finally
        {
            _questions.TryRemove(requestId, out _);
        }
    }

    public bool AnswerQuestion(Guid userId, Guid requestId, string? answer)
    {
        if (!_questions.TryGetValue(requestId, out var q) || q.UserId != userId)
            return false;
        return _questions.TryRemove(new KeyValuePair<Guid, Question>(requestId, q))
               && q.Answer.TrySetResult(answer is null ? null : Limit(answer, 4_000));
    }

    private async Task SendStatusAsync(Guid taskId, string label)
    {
        try
        {
            await _hub.Clients.Group($"task_{taskId}").SendAsync("OnAgentStatus", new { stage = "executing", label }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send phone status for task {TaskId}.", taskId);
        }
    }

    private static string Limit(string? value, int max)
    {
        var v = (value ?? string.Empty).Replace("\0", string.Empty);
        return v.Length <= max ? v : v[..max];
    }
}

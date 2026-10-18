using System.Collections.Concurrent;
using ConexyAI.Hub;
using ConexyAI.Repository;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Device;

// WEB_DEVICE: добавлено 2026-10-18 — подтверждения команд и вопросы ask_user, которые устройство
// (в т.ч. фоновый демон без консоли) переадресует в веб: устройство → сервер → веб → устройство.
// Хаб отвечает устройству сразу, а ожидание решения идёт в фоне: SignalR выполняет по одному вызову
// на клиента, и блокирующий метод хаба не дал бы устройству прислать ToolResult.

public interface IDeviceRemoteInteractionService
{
    /// <summary>Проверяет запрос подтверждения и запускает ожидание решения в фоне. False — отказ.</summary>
    Task<bool> RequestApprovalAsync(Guid userId, string connectionId, Guid requestId, Guid taskId,
        string? title, string? details, string? where, bool sensitive, CancellationToken ct);

    /// <summary>Проверяет вопрос, показывает его в вебе (<c>OnDeviceQuestion</c>) и ждёт ответ в фоне.</summary>
    Task<bool> RequestQuestionAsync(Guid userId, string connectionId, Guid requestId, Guid taskId,
        string? question, string[]? options, CancellationToken ct);

    /// <summary>Ответ из веба; принимается только от владельца вопроса.</summary>
    Task<bool> AnswerQuestionAsync(Guid userId, Guid requestId, string? answer);

    /// <summary>Устройство отключилось — его вопросы больше некому получать.</summary>
    void DropConnection(string connectionId);
}

public sealed class DeviceRemoteInteractionService : IDeviceRemoteInteractionService
{
    public static readonly TimeSpan QuestionTimeout = TimeSpan.FromMinutes(30);
    private const int MaxQuestionChars = 4_000;
    private const int MaxOptions = 10;
    private const int MaxOptionChars = 200;
    private const int MaxDetailsChars = 8_000;

    private sealed record PendingQuestion(Guid UserId, string ConnectionId, Guid TaskId, CancellationTokenSource Timeout);

    private readonly ConcurrentDictionary<Guid, PendingQuestion> _questions = new();
    private readonly IDeviceToolBridge _bridge;
    private readonly IPendingActionService _pendingActions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<DeviceRemoteInteractionService> _logger;

    public DeviceRemoteInteractionService(
        IDeviceToolBridge bridge,
        IPendingActionService pendingActions,
        IServiceScopeFactory scopeFactory,
        IHubContext<ConexyHub> hub,
        ILogger<DeviceRemoteInteractionService> logger)
    {
        _bridge = bridge;
        _pendingActions = pendingActions;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public async Task<bool> RequestApprovalAsync(Guid userId, string connectionId, Guid requestId, Guid taskId,
        string? title, string? details, string? where, bool sensitive, CancellationToken ct)
    {
        var chatId = await ResolveOwnedTaskChatAsync(userId, connectionId, taskId, ct);
        if (chatId is null)
            return false;

        var cleanTitle = (title ?? string.Empty).Trim();
        var body = Limit(details, MaxDetailsChars);
        var command = string.IsNullOrEmpty(cleanTitle)
                      || cleanTitle.Equals("Bash", StringComparison.OrdinalIgnoreCase)
                      || cleanTitle.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            ? body
            : $"[{Limit(cleanTitle, 100)}] {body}";
        var workingDirectory = Limit(where, 500);
        // Пока ждём решение, в чате видно, что агент не завис.
        await SendStatusAsync(taskId, sensitive ? "Жду вашего подтверждения (важное действие)…" : "Жду вашего подтверждения…");

        _ = Task.Run(async () =>
        {
            var approved = false;
            try
            {
                // Чувствительное действие никогда не одобряется автоматически, даже при «разрешить всё».
                approved = !sensitive && _pendingActions.IsTaskAutoApproved(taskId)
                    || await _pendingActions.WaitForDecisionAsync(
                        requestId, userId, chatId.Value, taskId, command, workingDirectory, sensitive, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Таймаут, отмена или сбой карточки — действие не выполняется.
                _logger.LogInformation("Device approval {RequestId} for task {TaskId} not approved: {Error}", requestId, taskId, ex.GetType().Name);
            }

            try
            {
                await _hub.Clients.Client(connectionId).SendAsync("DeviceApprovalResult", requestId, approved, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not deliver DeviceApprovalResult {RequestId}.", requestId);
            }
        }, CancellationToken.None);

        return true;
    }

    public async Task<bool> RequestQuestionAsync(Guid userId, string connectionId, Guid requestId, Guid taskId,
        string? question, string[]? options, CancellationToken ct)
    {
        if (await ResolveOwnedTaskChatAsync(userId, connectionId, taskId, ct) is null)
            return false;

        await SendStatusAsync(taskId, "Жду вашего ответа…");
        var timeout = new CancellationTokenSource(QuestionTimeout);
        if (!_questions.TryAdd(requestId, new PendingQuestion(userId, connectionId, taskId, timeout)))
        {
            timeout.Dispose();
            return false;
        }

        var cleanOptions = (options ?? Array.Empty<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Take(MaxOptions)
            .Select(o => Limit(o, MaxOptionChars))
            .ToArray();

        timeout.Token.Register(() =>
        {
            if (_questions.TryRemove(requestId, out var expired))
            {
                expired.Timeout.Dispose();
                _ = SendAnswerAsync(expired.ConnectionId, requestId, null);
            }
        });

        try
        {
            await _hub.Clients.Group($"task_{taskId}").SendAsync("OnDeviceQuestion", new
            {
                requestId,
                taskId,
                question = Limit(question, MaxQuestionChars),
                options = cleanOptions,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not broadcast device question {RequestId}.", requestId);
            if (_questions.TryRemove(requestId, out var failed))
            {
                failed.Timeout.Dispose();
                _ = SendAnswerAsync(connectionId, requestId, null);
            }
        }

        return true;
    }

    public async Task<bool> AnswerQuestionAsync(Guid userId, Guid requestId, string? answer)
    {
        if (!_questions.TryGetValue(requestId, out var pending) || pending.UserId != userId)
            return false;
        if (!_questions.TryRemove(new KeyValuePair<Guid, PendingQuestion>(requestId, pending)))
            return false;

        pending.Timeout.Dispose();
        await SendAnswerAsync(pending.ConnectionId, requestId, answer is null ? null : Limit(answer, MaxQuestionChars));
        return true;
    }

    /// <summary>Плашка текущего действия в чате: то же событие, что шлёт прогон агента.</summary>
    private async Task SendStatusAsync(Guid taskId, string label)
    {
        try
        {
            await _hub.Clients.Group($"task_{taskId}").SendAsync("OnAgentStatus", new { stage = "executing", label }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send device status for task {TaskId}.", taskId);
        }
    }

    public void DropConnection(string connectionId)
    {
        foreach (var (requestId, pending) in _questions)
        {
            if (pending.ConnectionId == connectionId && _questions.TryRemove(requestId, out _))
                pending.Timeout.Dispose();
        }
    }

    private async Task SendAnswerAsync(string connectionId, Guid requestId, string? answer)
    {
        try
        {
            await _hub.Clients.Client(connectionId).SendAsync("DeviceQuestionAnswer", requestId, answer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not deliver DeviceQuestionAnswer {RequestId}.", requestId);
        }
    }

    /// <summary>Вызывающий — зарегистрированное устройство владельца, задача существует и его. Возвращает chatId задачи.</summary>
    private async Task<Guid?> ResolveOwnedTaskChatAsync(Guid userId, string connectionId, Guid taskId, CancellationToken ct)
    {
        if (!_bridge.IsDeviceConnection(userId, connectionId))
            return null;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();
        var task = await repository.GetByIdAsNoTrackingAsync(taskId, ct);
        if (task is null || task.UserId != userId)
            return null;
        return task.ChatId ?? task.Id;
    }

    private static string Limit(string? value, int max)
    {
        var v = (value ?? string.Empty).Replace("\0", string.Empty);
        return v.Length <= max ? v : v[..max];
    }
}

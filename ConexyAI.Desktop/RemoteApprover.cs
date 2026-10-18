using System.Collections.Concurrent;
using ConexyAI.Desktop.Tools;
using Microsoft.AspNetCore.SignalR.Client;

namespace ConexyAI.Desktop;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// Подтверждения и вопросы для задач с сайта: запрос уходит через хаб (RequestDeviceApproval /
/// RequestDeviceQuestion), ответ приходит событием (DeviceApprovalResult / DeviceQuestionAnswer).
/// Запрещённые политикой команды отсекаются ещё в инструментах; правило из /permissions разрешает без вопроса.
/// При разрыве связи все ожидания завершаются отказом.
/// </summary>
public sealed class RemoteApprover : ICommandApprover
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _approvals = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string?>> _questions = new();
    private HubConnection? _hub;

    /// <summary>Подключает к соединению сессии: подписка на ответы и отмена ожиданий при разрыве.</summary>
    public void Attach(HubConnection hub)
    {
        _hub = hub;
        hub.On<Guid, bool>("DeviceApprovalResult", (requestId, approved) =>
        {
            if (_approvals.TryRemove(requestId, out var tcs))
                tcs.TrySetResult(approved);
        });
        hub.On<Guid, string?>("DeviceQuestionAnswer", (requestId, answer) =>
        {
            if (_questions.TryRemove(requestId, out var tcs))
                tcs.TrySetResult(answer);
        });
        hub.Reconnecting += _ => { CancelAll(); return Task.CompletedTask; };
        hub.Closed += _ => { CancelAll(); return Task.CompletedTask; };
    }

    public void CancelAll()
    {
        foreach (var id in _approvals.Keys)
            if (_approvals.TryRemove(id, out var tcs))
                tcs.TrySetResult(false);
        foreach (var id in _questions.Keys)
            if (_questions.TryRemove(id, out var tcs))
                tcs.TrySetResult(null);
    }

    public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) =>
        ApproveAsync("Bash", command, workingDirectory, ct);

    public Task<bool> ApproveAsync(string title, string details, string workingDirectory, CancellationToken ct)
    {
        if (title is "Bash" or "cmd" && ToolCallContext.Current is { } ctx
            && PermissionStore.Load().Match(ctx.ProjectRoot, details) is not null)
            return Task.FromResult(true);
        return RequestAsync(title, details, workingDirectory, sensitive: false, ct);
    }

    public Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        RequestAsync(title, details, where, sensitive: true, ct);

    private async Task<bool> RequestAsync(string title, string details, string where, bool sensitive, CancellationToken ct)
    {
        if (_hub is not { State: HubConnectionState.Connected } hub || ToolCallContext.Current is not { } ctx)
            return false;
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[requestId] = tcs; // до вызова: ответ может прийти раньше, чем вернётся Invoke
        try
        {
            if (!await hub.InvokeAsync<bool>("RequestDeviceApproval", requestId, ctx.TaskId, title, details, where, sensitive, ct))
                return false;
            await using var reg = ct.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _approvals.TryRemove(requestId, out _);
        }
    }

    public async Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct)
    {
        if (_hub is not { State: HubConnectionState.Connected } hub || ToolCallContext.Current is not { } ctx)
            return null;
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questions[requestId] = tcs;
        try
        {
            await hub.InvokeAsync("RequestDeviceQuestion", requestId, ctx.TaskId, question, options, ct);
            await using var reg = ct.Register(() => tcs.TrySetResult(null));
            return await tcs.Task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _questions.TryRemove(requestId, out _);
        }
    }
}

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// Выбирает, кого спрашивать, по контексту вызова: задачи с сайта (и все вызовы в режиме демона, где консоли нет) —
/// на сайте, собственные ходы интерактивного сеанса — в консоли.
/// </summary>
public sealed class RoutingApprover(ICommandApprover? console, RemoteApprover remote) : ICommandApprover
{
    private ICommandApprover Target =>
        console is null || ToolCallContext.Current is { Remote: true } ? remote : console;

    public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) =>
        Target.ApproveAsync(command, workingDirectory, ct);

    public Task<bool> ApproveAsync(string title, string details, string workingDirectory, CancellationToken ct) =>
        Target.ApproveAsync(title, details, workingDirectory, ct);

    public Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        Target.ApproveSensitiveAsync(title, details, where, ct);

    public Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct) =>
        Target.AskUserAsync(question, options, ct);
}

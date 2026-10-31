using System.Collections.Concurrent;
using ConexyAI.Desktop.Tools;
using Microsoft.AspNetCore.SignalR;
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

    // BASH_DESCRIPTION: описание едет на сайт отдельным полем — в карточке оно над командой.
    public Task<bool> ApproveCommandAsync(string command, string? description, string workingDirectory, CancellationToken ct)
    {
        if (ToolCallContext.Current is { } ctx
            && PermissionStore.Load().Match(ctx.ProjectRoot, command) is not null)
            return Task.FromResult(true);
        return RequestAsync("Bash", command, workingDirectory, sensitive: false, ct, description);
    }

    public Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        RequestAsync(title, details, where, sensitive: true, ct);

    private async Task<bool> RequestAsync(string title, string details, string where, bool sensitive, CancellationToken ct,
        string? description = null)
    {
        if (_hub is not { State: HubConnectionState.Connected } hub || ToolCallContext.Current is not { } ctx)
            return false;
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[requestId] = tcs; // до вызова: ответ может прийти раньше, чем вернётся Invoke
        try
        {
            try
            {
                // SIGNALR_NO_OVERLOADS: описание идёт отдельным именем метода — перегрузки в хабе запрещены.
                if (!await hub.InvokeAsync<bool>("RequestDeviceApprovalWithDescription",
                        requestId, ctx.TaskId, title, details, where, sensitive, description, ct))
                    return false;
            }
            catch (Exception ex) when (ex is HubException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                // Старый сервер такого метода не знает ('Method does not exist') — просим без описания.
                if (!await hub.InvokeAsync<bool>("RequestDeviceApproval", requestId, ctx.TaskId, title, details, where, sensitive, ct))
                    return false;
            }

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
/// Выбирает, кого спрашивать.
/// REMOTE_APPROVAL_TRUST: удалённое подтверждение — это просто bool от хаба без криптографической подписи
/// пользователя: скомпрометированный сервер (или тот, кто украл сессию сайта) может «одобрить» сам.
/// Поэтому:
///  • в интерактивном сеансе ВСЕ подтверждения спрашиваются в локальной консоли, даже если сервер
///    пометил вызов как удалённый (ToolRemote) — маршрутизация сервера для подтверждений игнорируется;
///    на сайт уходят только вопросы ask_user задач с сайта (это не разрешения);
///  • в режиме демона (консоли нет) подтверждения идут на сайт, но самые опасные категории
///    (установка программ, отправка писем, команды с путями вне проекта, сторонние MCP) отклоняются без
///    запроса, пока пользователь локально не включит их: `conexy remote full` (ClientConfig.RemoteApprovalLevel).
/// </summary>
public sealed class RoutingApprover(ICommandApprover? console, RemoteApprover remote, Func<string>? remoteLevel = null) : ICommandApprover
{
    // Вопросы (не разрешения) по-прежнему идут туда, откуда пришла задача.
    private ICommandApprover QuestionTarget =>
        console is null || ToolCallContext.Current is { Remote: true } ? remote : console;

    private string Level => remoteLevel?.Invoke() ?? ClientConfig.LoadRemoteApprovalLevel();

    /// <summary>Почему демон не спрашивает сайт об этом действии (null — можно спросить).</summary>
    internal static string? RemoteRefusal(string level, string title, string details, bool sensitive)
    {
        if (level.Equals(ClientConfig.RemoteLevelFull, StringComparison.OrdinalIgnoreCase))
            return null;
        var ctx = ToolCallContext.Current;
        string? category = null;
        if (ctx?.Tool is "install_software" || title.StartsWith("Установка", StringComparison.Ordinal))
            category = "установка программ";
        else if (sensitive && title.StartsWith("Отправка письма", StringComparison.Ordinal)
                 || ctx?.Tool is { } t && (t.EndsWith("_send", StringComparison.Ordinal) || t.EndsWith("__send", StringComparison.Ordinal)))
            category = "отправка писем";
        else if (title.StartsWith("MCP «", StringComparison.Ordinal))
            category = "сторонние MCP-инструменты";
        else if (title is "Bash" or "cmd" or "Фоновый процесс" && ctx is { ProjectRoot: { } root }
                 && CommandPolicy.ReferencesOutside(details, root))
            category = "команды с путями вне проекта";
        else if (title is "Чтение вне проекта" or "Копирование в проект")
            category = "чтение файлов вне проекта";
        return category is null
            ? null
            : $"Действие отклонено клиентом: {category} по подтверждению с сайта запрещены настройками этого ПК. " +
              "Владелец ПК может разрешить командой `conexy remote full` или выполнить задачу в интерактивном `conexy`.";
    }

    private async Task<bool> RemoteAsync(string title, string details, bool sensitive, Func<Task<bool>> ask)
    {
        if (RemoteRefusal(Level, title, details, sensitive) is { } why)
        {
            Daemon.Log("  ✖ " + why);
            return false;
        }
        return await ask();
    }

    public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) =>
        console?.ApproveAsync(command, workingDirectory, ct)
        ?? RemoteAsync("Bash", command, false, () => remote.ApproveAsync(command, workingDirectory, ct));

    public Task<bool> ApproveAsync(string title, string details, string workingDirectory, CancellationToken ct) =>
        console?.ApproveAsync(title, details, workingDirectory, ct)
        ?? RemoteAsync(title, details, false, () => remote.ApproveAsync(title, details, workingDirectory, ct));

    public Task<bool> ApproveCommandAsync(string command, string? description, string workingDirectory, CancellationToken ct) =>
        console?.ApproveCommandAsync(command, description, workingDirectory, ct)
        ?? RemoteAsync("Bash", command, false, () => remote.ApproveCommandAsync(command, description, workingDirectory, ct));

    public Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        console?.ApproveSensitiveAsync(title, details, where, ct)
        ?? RemoteAsync(title, details, true, () => remote.ApproveSensitiveAsync(title, details, where, ct));

    public Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct) =>
        QuestionTarget.AskUserAsync(question, options, ct);
}

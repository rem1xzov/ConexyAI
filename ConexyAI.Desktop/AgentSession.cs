using System.Text.Json;
using ConexyAI.Desktop.Tools;
using Microsoft.AspNetCore.SignalR.Client;

namespace ConexyAI.Desktop;

/// <summary>
/// Живое соединение с сервером: регистрирует этот ПК как устройство, выполняет присланные
/// инструменты локально и стримит ответ агента в консоль.
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly ServerClient _server;
    private readonly ToolRunner _tools;
    private readonly Guid _chatId;
    private readonly HubConnection _hub;

    private TaskCompletionSource<string?>? _turn;
    private CancellationTokenSource? _turnCts;
    private bool _streamedAnything;
    private volatile bool _needBullet;

    /// <summary>Оркестр агентов (субагенты): сервер сам решает, разрешён ли он на тарифе.</summary>
    public bool Orchestra { get; set; }

    public AgentSession(ServerClient server, ToolRunner tools, Guid chatId)
    {
        _server = server;
        _tools = tools;
        _chatId = chatId;

        _hub = new HubConnectionBuilder()
            .WithUrl($"{server.BaseUrl}/hubs/conexy", options => options.AccessTokenProvider = async () => await server.GetTokenAsync())
            .WithAutomaticReconnect()
            .Build();

        _hub.On<Guid, Guid, string, string>("ExecuteTool", OnExecuteToolAsync);
        // Последний аргумент событий группы — id группы (scopeId); он нам не нужен.
        _hub.On<string, string?>("OnContentToken", (delta, _) =>
        {
            _streamedAnything = true;
            Ui.Idle();
            if (_needBullet)
            {
                _needBullet = false;
                Ui.AssistantStart();
            }
            Console.Write(delta);
        });
        _hub.On<JsonElement, string?>("ToolAction", (payload, _) => OnServerToolAction(payload));
        _hub.On<JsonElement, string?>("AgentTeam", (payload, _) => OnTeam(payload));
        _hub.On<JsonElement, string?>("OnAgentStatus", (payload, _) => OnAgentStatus(payload));
        _hub.On<JsonElement, string?>("OnCompleted", (_, _) => _turn?.TrySetResult(null));
        _hub.On<string, string?>("OnError", (message, _) => _turn?.TrySetResult(message));
        _hub.On<Guid, string?>("OnStopped", (_, _) => _turn?.TrySetResult("Остановлено."));

        _hub.Reconnected += async _ => await RegisterAsync();
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _hub.StartAsync(ct);
        await RegisterAsync();
        await _hub.InvokeAsync("JoinTask", _chatId, ct);
    }

    private Task RegisterAsync() => _hub.InvokeAsync(
        "RegisterDevice",
        Environment.MachineName,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        _tools.Root);

    /// <summary>Один ход агента. Возвращает текст ошибки или null при успехе.</summary>
    public async Task<string?> AskAsync(string prompt, CancellationToken ct)
    {
        _turn = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamedAnything = false;
        _needBullet = true;
        Ui.Busy();

        var taskId = await _server.StartRunAsync(prompt, _chatId, Orchestra, ct);
        await _hub.InvokeAsync("JoinTask", taskId, ct);

        // Страховка: событие завершения могло прийти до подписки на группу задачи.
        _ = Task.Run(() => WatchdogAsync(taskId, _turnCts.Token));

        try
        {
            await using var registration = ct.Register(() =>
            {
                _ = _hub.InvokeAsync("StopGeneration", taskId);
                _turn.TrySetResult("Остановлено.");
            });
            return await _turn.Task;
        }
        finally
        {
            _turnCts.Cancel();
            Ui.Idle();
            Console.WriteLine();
        }
    }

    private async Task WatchdogAsync(Guid taskId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                var run = await _server.GetRunAsync(taskId, ct);
                if (run is null || run.Status is not ("Completed" or "Failed" or "Cancelled"))
                    continue;

                if (!_streamedAnything && !string.IsNullOrEmpty(run.Result))
                {
                    Ui.AssistantStart();
                    Console.Write(run.Result);
                }
                _turn?.TrySetResult(run.Status == "Completed" ? null : $"Задача завершилась со статусом {run.Status}.");
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
    }

    private static string? Prop(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in el.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    // Инструменты, которые выполняет сам сервер (поиск в интернете и т.п.): сайт рисует для них карточку по событию
    // ToolAction, в консоли рисуем то же. Десктопные инструменты (bash, файлы) показывает клиент сам.
    private static readonly HashSet<string> ServerTools =
        ["web_search", "fetch_web_page", "search_user_chats", "search_documents"];

    private void OnServerToolAction(JsonElement payload)
    {
        var tool = Prop(payload, "toolName");
        if (tool is null || !ServerTools.Contains(tool))
            return;
        var agent = Prop(payload, "agentId");
        if (agent is not (null or "lead"))
            return; // действия помощников — в их строках статуса

        switch (Prop(payload, "status"))
        {
            case "started":
                Ui.ToolCall($"{tool} {Prop(payload, "command")}");
                break;
            case "completed" or "failed":
                Ui.Idle();
                Ui.ShowToolResult(new ToolResult(Prop(payload, "summary") ?? string.Empty, Prop(payload, "status") == "failed"));
                _needBullet = true;
                Ui.Busy();
                break;
        }
    }

    private void OnTeam(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return;
        var agents = new List<(string Id, string Role, string Brief, string Scope)>();
        foreach (var p in payload.EnumerateObject())
        {
            if (!p.Name.Equals("agents", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var a in p.Value.EnumerateArray())
            {
                var scope = new List<string>();
                foreach (var sp in a.EnumerateObject())
                    if (sp.Name.Equals("scope", StringComparison.OrdinalIgnoreCase) && sp.Value.ValueKind == JsonValueKind.Array)
                        scope.AddRange(sp.Value.EnumerateArray().Select(s => s.GetString() ?? string.Empty));
                agents.Add((Prop(a, "id") ?? "?", Prop(a, "role") ?? "agent", Prop(a, "brief") ?? string.Empty, string.Join(", ", scope)));
            }
        }

        Ui.TeamRoster(agents);
        Ui.Busy("Команда работает");
    }

    private void OnAgentStatus(JsonElement payload)
    {
        var agentId = Prop(payload, "agentId");
        if (agentId is null or "lead")
            return;
        Ui.AgentStatus(agentId, Prop(payload, "agentRole") ?? agentId, Prop(payload, "stage") ?? string.Empty, Prop(payload, "label") ?? string.Empty);
    }

    private async Task OnExecuteToolAsync(Guid callId, Guid taskId, string tool, string argumentsJson)
    {
        ToolResult result;
        try
        {
            result = await _tools.RunAsync(tool, argumentsJson, _turnCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail("Внутренняя ошибка клиента: " + ex.Message);
        }

        Ui.Idle();
        Ui.ShowToolResult(result);
        _needBullet = true;
        Ui.Busy();

        await _hub.InvokeAsync("ToolResult", callId, result.Output, result.IsError);
    }

    public async ValueTask DisposeAsync()
    {
        _turnCts?.Dispose();
        await _hub.DisposeAsync();
    }
}

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
    // WEB_DEVICE: добавлено 2026-10-18 — в режиме демона нет ни своего ToolRunner, ни чата.
    private readonly ToolRunner? _tools;
    private readonly Guid? _chatId;
    private readonly HubConnection _hub;
    private readonly DeviceTools _device;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // WEB_DEVICE: сервер до ExecuteTool сообщает проект вызова (ToolProject) и что он с сайта (ToolRemote).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _callProjects = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _callRemote = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _announcedTasks = new();

    /// <summary>Завершается, когда соединение закрыто окончательно (переподключение сдалось).</summary>
    public Task Closed => _closed.Task;

    // ORCHESTRA: сервер сообщает (событием ToolAgent перед ExecuteTool), какому субагенту принадлежит вызов.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Id, string Role)> _callAgents = new();

    private TaskCompletionSource<string?>? _turn;
    private CancellationTokenSource? _turnCts;
    private bool _streamedAnything;
    private volatile bool _needBullet;

    /// <summary>Оркестр агентов (субагенты): сервер сам решает, разрешён ли он на тарифе.</summary>
    public bool Orchestra { get; set; }

    /// <summary>Агент чата: "coder" (код, терминал) или "cowork" (исследования, документы, почта).</summary>
    public string Mode { get; set; } = "coder";

    /// <summary>Инструменты ПК сверх файлов: Google и локальные MCP-серверы.</summary>
    public Mcp.LocalTools? Local { get; set; }

    /// <summary>Текст ответа агента за последний ход (для /compact и режима плана).</summary>
    public string LastAnswer => _answer.ToString();
    private readonly System.Text.StringBuilder _answer = new();

    /// <summary>
    /// Приписка к следующему сообщению (служебный контекст для агента): «пользователь откатил файлы», пересказ
    /// прошлого чата после /compact. Статическая, чтобы пережить смену чата.
    /// </summary>
    public static string? PendingNote { get; set; }

    /// <summary>Режим плана: к сообщению добавляется инструкция, а клиент блокирует правки.</summary>
    public bool PlanMode
    {
        get => _tools?.PlanMode ?? false;
        set { if (_tools is not null) _tools.PlanMode = value; }
    }

    public const string PlanPrefix =
        "[РЕЖИМ ПЛАНА] Ничего не меняй. Изучи проект (чтение, поиск, просмотр, команды только на чтение) и предложи " +
        "подробный пошаговый план: какие файлы и что изменить, какие команды выполнить, риски и как проверить результат. " +
        "Правки файлов и изменяющие команды заблокированы до одобрения плана.\n\n";

    public AgentSession(ServerClient server, ToolRunner? tools, Guid? chatId, DeviceTools device)
    {
        _server = server;
        _tools = tools;
        _chatId = chatId;
        _device = device;

        _hub = new HubConnectionBuilder()
            .WithUrl($"{server.BaseUrl}/hubs/conexy", options => options.AccessTokenProvider = async () => await server.GetTokenAsync())
            // WEB_DEVICE: изменено 2026-10-18 — переподключаемся бесконечно (демон живёт сутками).
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(120);
        _hub.Closed += _ => { _closed.TrySetResult(); return Task.CompletedTask; };
        device.Remote.Attach(_hub);
        _hub.On<Guid, string>("ToolProject", (callId, projectId) =>
        {
            _callProjects[callId] = projectId;
            return Task.CompletedTask;
        });
        _hub.On<Guid>("ToolRemote", callId =>
        {
            _callRemote[callId] = true;
            return Task.CompletedTask;
        });

        // Обработчик события исполняется в цикле приёма SignalR: пока он ждёт долгую команду (npm install,
        // сборка, подтверждение человеком), клиент не читает ping сервера и через 30 с рвёт соединение —
        // это и было «bash отваливается». Поэтому инструмент запускаем в фоне и сразу освобождаем цикл.
        _hub.On<Guid, string, string>("ToolAgent", (callId, agentId, role) =>
        {
            _callAgents[callId] = (agentId, role);
            return Task.CompletedTask;
        });
        _hub.On<Guid, Guid, string, string>("ExecuteTool", (callId, taskId, tool, args) =>
        {
            _ = Task.Run(() => OnExecuteToolAsync(callId, taskId, tool, args));
            return Task.CompletedTask;
        });
        // Последний аргумент событий группы — id группы (scopeId); он нам не нужен.
        _hub.On<string, string?>("OnContentToken", (delta, _) =>
        {
            _streamedAnything = true;
            _answer.Append(delta);
            Ui.Idle();
            if (_needBullet)
            {
                _needBullet = false;
                Ui.AssistantStart();
            }
            Ui.Md.Feed(delta);
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
        if (_chatId is { } chatId)
            await _hub.InvokeAsync("JoinTask", chatId, ct);
    }

    // WEB_DEVICE: изменено 2026-10-18 — после устройства регистрируем и список проектов. Демон вызывает это раз в минуту:
    // на сервере побеждает последняя регистрация, и после выхода из интерактивного сеанса демон вернёт задачи себе.
    public async Task RegisterAsync()
    {
        _device.ReloadProjects();
        await _hub.InvokeAsync(
            "RegisterDevice",
            Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription + (Tools.ShellTool.HasPwsh ? " · PowerShell 7" : string.Empty),
            _tools?.Root ?? _device.DefaultRoot ?? string.Empty);
        try
        {
            await _hub.InvokeAsync("RegisterDeviceProjects", _device.Projects.ToJson());
        }
        catch (Microsoft.AspNetCore.SignalR.HubException)
        {
            // Старый сервер без проектов устройства — работаем без них.
        }
    }

    /// <summary>Один ход агента. Возвращает текст ошибки или null при успехе.</summary>
    public async Task<string?> AskAsync(string prompt, CancellationToken ct)
    {
        _turn = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamedAnything = false;
        _needBullet = true;
        _answer.Clear();
        Ui.Busy();

        _tools!.Checkpoints.BeginTurn(prompt);
        if (PendingNote is { } note)
        {
            prompt = note + "\n\n" + prompt;
            PendingNote = null;
        }
        if (PlanMode)
            prompt = PlanPrefix + prompt;

        var taskId = await _server.StartRunAsync(
            prompt, _chatId!.Value, Orchestra && Mode == "coder", Attachments.Pending.ToList(), ct,
            model: Mode == "cowork" ? "conexy-cowork" : "conexy-coder",
            deviceTools: Local?.Specs());
        Attachments.Clear(); // ушли с этим ходом — следующему сообщению не нужны
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
            Ui.TeamEnd();
            Ui.FlushText();
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
                    Ui.Md.Feed(run.Result);
                    Ui.FlushText();
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
        (string Id, string Role)? agent = _callAgents.TryRemove(callId, out var a) ? a : null;
        Ui.CurrentAgent.Value = agent;

        // WEB_DEVICE: добавлено 2026-10-18 — выбор проекта и того, кто подтверждает (консоль или сайт).
        var remote = _callRemote.TryRemove(callId, out _) || _device.Daemon;
        var projectId = _callProjects.TryRemove(callId, out var pid) ? pid : null;
        ToolRunner? runner = null;
        ToolResult? routeError = null;
        if (projectId is not null)
        {
            var root = _device.RootOf(projectId);
            if (root is null || !Directory.Exists(root))
                routeError = ToolResult.Fail("Проект не найден на этом ПК");
            else
                runner = _device.RunnerFor(root);
        }
        else if (remote)
        {
            var root = _device.DefaultRoot;
            if (root is null || !Directory.Exists(root))
                routeError = ToolResult.Fail("На этом ПК не настроен проект по умолчанию (conexy projects add <папка>).");
            else
                runner = _device.RunnerFor(root);
        }
        else
        {
            runner = _tools;
        }

        if (remote && !_device.Daemon && _announcedTasks.TryAdd(taskId, true))
            Ui.Info($"  ◦ с сайта выполняется задача {taskId.ToString()[..8]} на этом ПК ({runner?.Root ?? "—"})");

        ToolResult result;
        try
        {
            if (routeError is not null || runner is null)
                result = routeError ?? ToolResult.Fail("Нет рабочей папки для вызова.");
            else
            {
                ToolCallContext.Current = new ToolCallInfo(taskId, remote, runner.Root);
                var token = remote ? _lifetime.Token : _turnCts?.Token ?? CancellationToken.None;
                result = await runner.RunAsync(tool, argumentsJson, token);
            }
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail("Внутренняя ошибка клиента: " + ex.Message);
        }
        finally
        {
            ToolCallContext.Current = null;
        }

        if (remote)
        {
            // Задачи с сайта не печатают результат в консоль (их видно на сайте); демон пишет в журнал.
            if (_device.Daemon && tool is not ("project_rules" or "turn_diff"))
                _device.Log($"  → {(result.IsError ? "ошибка" : "ok")}: {FirstLine(result.Output)}");
            await SendResultAsync(callId, result);
            return;
        }

        var shown = result.Output.StartsWith("IMAGE_BASE64:", StringComparison.Ordinal)
            ? ToolResult.Ok($"скриншот снят ({result.Output.Length * 3 / 4 / 1024} КБ), отправлен агенту")
            : result.Output.StartsWith("FILE_BASE64:", StringComparison.Ordinal)
                ? ToolResult.Ok($"файл прочитан ({result.Output.Length * 3 / 4 / 1024} КБ), отправлен агенту")
                : result;
        if (agent is { } worker)
        {
            // Итог субагента — в его карточку на панели команды.
            Ui.AgentToolResult(worker.Id, worker.Role, shown);
            Ui.Busy();
        }
        else
        {
            Ui.Idle();
            Ui.ShowToolResult(shown);
            _needBullet = true;
            Ui.Busy();
        }

        await SendResultAsync(callId, result);
    }

    private static string FirstLine(string text)
    {
        if (text.StartsWith("IMAGE_BASE64:", StringComparison.Ordinal) || text.StartsWith("FILE_BASE64:", StringComparison.Ordinal))
            return $"данные ({text.Length / 1024} КБ)";
        var line = text.Split('\n', 2)[0];
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private async Task SendResultAsync(Guid callId, ToolResult result)
    {
        // Результат не должен теряться при кратком разрыве: ждём переподключения и повторяем.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _hub.InvokeAsync("ToolResult", callId, result.Output, result.IsError);
                return;
            }
            catch (Exception) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                if (_device.Daemon)
                    _device.Log("Не удалось отправить результат инструмента: " + ex.Message);
                else
                    Ui.Error("Не удалось отправить результат инструмента: " + ex.Message);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _device.Remote.CancelAll();
        _turnCts?.Dispose();
        await _hub.DisposeAsync();
        _lifetime.Dispose();
    }
}

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>Переподключение без предела попыток: 0, 2, 4, 8 … с, не реже раза в минуту.</summary>
public sealed class ForeverRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 6))));
}

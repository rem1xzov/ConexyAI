using ConexyAI.Contract;
using System.Collections.Concurrent;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Device;

// DESKTOP_AGENT: добавлено 2026-10-15 — исполнение инструментов агента на ПК пользователя.
// Сервер ведёт агентский цикл, а файлы и команды выполняет десктопный клиент: сюда приходит вызов,
// он уходит на подключённое устройство владельца, а результат возвращается через хаб.

/// <summary>Результат инструмента, выполненного на устройстве.</summary>
public sealed record DeviceToolOutcome(string Output, bool IsError);

/// <summary>Сведения о подключённом устройстве — для промпта агента.</summary>
public sealed record DeviceInfo(string Name, string Os, string Workspace, IReadOnlyList<DeviceProject>? Projects = null);

// WEB_DEVICE: добавлено 2026-10-18 — проект (папка) на ПК, который можно выбрать из веба.
public sealed record DeviceProject(string Id, string Name, string? Path);

public interface IDeviceToolBridge
{
    DeviceInfo? GetInfo(Guid userId);
    /// <summary>Подключено ли хотя бы одно устройство пользователя.</summary>
    bool IsOnline(Guid userId);

    /// <summary>Регистрирует подключение как устройство пользователя.</summary>
    void Register(Guid userId, string connectionId, string deviceName, string? os = null, string? workspace = null);

    /// <summary>
    /// Чем является подключение: <c>interactive</c> (окно клиента) или <c>daemon</c>. Интерактивное окно
    /// важнее фонового демона, иначе демон каждую минуту перехватывает задачи и выполняет их в своей папке.
    /// </summary>
    bool SetMode(Guid userId, string connectionId, string? mode);

    /// <summary>Снимает подключение при разрыве; ждущие вызовы этого устройства завершаются ошибкой.</summary>
    void Unregister(string connectionId);

    // WEB_DEVICE: добавлено 2026-10-18
    /// <summary>Список проектов устройства (JSON-массив <c>[{id,name,path?}]</c>). False — подключение не устройство пользователя.</summary>
    bool RegisterProjects(Guid userId, string connectionId, string? projectsJson);

    /// <summary>Является ли подключение зарегистрированным устройством пользователя.</summary>
    bool IsDeviceConnection(Guid userId, string connectionId);

    // LOCAL_MCP: инструменты, которые предлагает ПК (Google, почта, локальные MCP) — агент с сайта должен их знать.
    /// <summary>Регистрирует локальные инструменты устройства (JSON-массив <c>[{name,description,inputSchema}]</c>).</summary>
    bool RegisterTools(Guid userId, string connectionId, string? toolsJson);

    /// <summary>Инструменты подключённого устройства или null.</summary>
    IReadOnlyList<DeviceToolSpec>? GetTools(Guid userId);

    /// <summary>
    /// Отправляет инструмент на устройство и ждёт результат. WEB_DEVICE: <paramref name="projectId"/> уходит
    /// событием <c>ToolProject</c>, <paramref name="remoteApprovals"/> — событием <c>ToolRemote</c> (оба ДО ExecuteTool).
    /// </summary>
    Task<DeviceToolOutcome> ExecuteAsync(Guid userId, Guid taskId, string tool, string argumentsJson, CancellationToken ct,
        string? projectId = null, bool remoteApprovals = false);

    /// <summary>Принимает результат от устройства. Чужое подключение результат принять не может.</summary>
    bool Complete(string connectionId, Guid callId, string output, bool isError);
}

public sealed class DeviceToolBridge : IDeviceToolBridge
{
    /// <summary>
    /// Сколько сервер ждёт ответ устройства (подтверждение команды человеком входит в это время).
    /// Должно быть больше максимального таймаута команды на ПК (60 мин) с запасом на подтверждение — иначе
    /// долгая сборка обрывалась сервером «устройство не ответило», хотя на ПК всё ещё шло.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(70);

    private const int MaxOutputChars = 200_000;

    /// <summary>Скриншот приходит base64-строкой с этим префиксом — его нельзя обрезать до обычного лимита.</summary>
    public const string ImagePrefix = "IMAGE_BASE64:";

    /// <summary>Байты файла (file_read_bytes) — base64 с этим префиксом, тоже без обрезки до обычного лимита.</summary>
    public const string FilePrefix = "FILE_BASE64:";

    private const int MaxImageChars = 12_000_000;

    private sealed record Device(string ConnectionId, string Name, string Os, string Workspace, long Seq = 0, string Mode = "")
    {
        public IReadOnlyList<DeviceProject> Projects { get; init; } = Array.Empty<DeviceProject>();
        public IReadOnlyList<DeviceToolSpec> Tools { get; init; } = Array.Empty<DeviceToolSpec>();
    }

    /// <summary>Все подключения пользователя: одно окно могло переподключиться, пока демон ещё жив.</summary>
    private sealed class DeviceSet
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, Device> ByConnection = new(StringComparer.Ordinal);
        public string? ActiveId;
        public DateTime LastSeenAt = DateTime.UtcNow;

        /// <summary>Интерактивное окно важнее неизвестного клиента, а тот — важнее демона; из равных — последнее.</summary>
        public void RecomputeActive()
        {
            Device? best = null;
            var bestRank = -1;
            foreach (var device in ByConnection.Values)
            {
                var rank = device.Mode == "interactive" ? 2 : device.Mode == "daemon" ? 0 : 1;
                if (rank > bestRank || rank == bestRank && best is not null && device.Seq > best.Seq)
                {
                    best = device;
                    bestRank = rank;
                }
            }
            ActiveId = best?.ConnectionId;
        }
    }

    // WEB_DEVICE: пределы списка проектов — он попадает в промпт и в ответ /device/status.
    public const int MaxProjects = 50;
    private const int MaxProjectNameChars = 100;

    private sealed record Pending(string ConnectionId, TaskCompletionSource<DeviceToolOutcome> Completion);

    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<DeviceToolBridge> _logger;
    private readonly ConcurrentDictionary<Guid, DeviceSet> _devices = new();
    private long _sequence;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    public DeviceToolBridge(IHubContext<ConexyHub> hub, ILogger<DeviceToolBridge> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public bool IsOnline(Guid userId) => Active(userId) is not null;

    /// <summary>Подключение пропало меньше 30 секунд назад — вероятно, переподключается.</summary>
    private bool RecentlySeen(Guid userId) =>
        _devices.TryGetValue(userId, out var set) && DateTime.UtcNow - set.LastSeenAt < TimeSpan.FromSeconds(30);

    public DeviceInfo? GetInfo(Guid userId) =>
        Active(userId) is { } d ? new DeviceInfo(d.Name, d.Os, d.Workspace, d.Projects) : null;

    public bool IsDeviceConnection(Guid userId, string connectionId) =>
        _devices.TryGetValue(userId, out var set) && set.ByConnection.ContainsKey(connectionId);

    /// <summary>Подключение, которому сейчас уходят задачи: интерактивное окно важнее демона.</summary>
    private Device? Active(Guid userId)
    {
        if (!_devices.TryGetValue(userId, out var set))
            return null;
        lock (set.Sync)
            return set.ActiveId is { } id && set.ByConnection.TryGetValue(id, out var device) ? device : null;
    }

    public bool RegisterProjects(Guid userId, string connectionId, string? projectsJson)
    {
        var projects = ParseProjects(projectsJson);
        if (!Update(userId, connectionId, d => d with { Projects = projects }))
            return false;
        _logger.LogInformation("Desktop device projects: user={UserId} count={Count}", userId, projects.Count);
        return true;
    }

    /// <summary>Обновляет данные конкретного подключения (не обязательно активного).</summary>
    private bool Update(Guid userId, string connectionId, Func<Device, Device> update)
    {
        if (!_devices.TryGetValue(userId, out var set))
            return false;
        lock (set.Sync)
        {
            if (!set.ByConnection.TryGetValue(connectionId, out var device))
                return false;
            set.ByConnection[connectionId] = update(device);
            set.RecomputeActive();
            return true;
        }
    }

    // WEB_DEVICE: добавлено 2026-10-18 — клиенту не доверяем: одна строка без управляющих символов, без дублей id.
    public static IReadOnlyList<DeviceProject> ParseProjects(string? json)
    {
        var result = new List<DeviceProject>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (result.Count >= MaxProjects)
                    break;
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                var id = Clean(Str(item, "id"), 100);
                if (id.Length == 0 || result.Any(p => p.Id == id))
                    continue;
                var name = Clean(Str(item, "name"), MaxProjectNameChars);
                var path = Clean(Str(item, "path"), 260);
                result.Add(new DeviceProject(id, name.Length == 0 ? id : name, path.Length == 0 ? null : path));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Битый JSON — просто пустой список.
        }
        return result;

        static string? Str(System.Text.Json.JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
    }

    private static string Clean(string? v, int max) =>
        new string((v ?? string.Empty).Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();

    public bool RegisterTools(Guid userId, string connectionId, string? toolsJson)
    {
        var tools = DeviceToolSanitizer.Clean(ParseTools(toolsJson)) ?? new List<DeviceToolSpec>();
        if (!Update(userId, connectionId, d => d with { Tools = tools }))
            return false;
        _logger.LogInformation("Desktop device tools: user={UserId} count={Count}", userId, tools.Count);
        return true;
    }

    public bool SetMode(Guid userId, string connectionId, string? mode)
    {
        var clean = mode is "interactive" or "daemon" ? mode : string.Empty;
        if (!Update(userId, connectionId, d => d with { Mode = clean }))
            return false;
        _logger.LogInformation("Desktop device mode: user={UserId} mode={Mode}", userId, clean.Length == 0 ? "unknown" : clean);
        return true;
    }

    public IReadOnlyList<DeviceToolSpec>? GetTools(Guid userId) =>
        Active(userId) is { Tools.Count: > 0 } d ? d.Tools : null;

    /// <summary>JSON с клиента — только для чтения: имя, описание и схема (чистит DeviceToolSanitizer).</summary>
    private static List<DeviceToolSpec> ParseTools(string? json)
    {
        var result = new List<DeviceToolSpec>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                result.Add(new DeviceToolSpec
                {
                    Name = item.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null,
                    Description = item.TryGetProperty("description", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString() : null,
                    InputSchema = item.TryGetProperty("inputSchema", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Object ? s.Clone() : null,
                });
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Битый JSON — инструментов нет.
        }
        return result;
    }

    public void Register(Guid userId, string connectionId, string deviceName, string? os = null, string? workspace = null)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim();
        var seq = Interlocked.Increment(ref _sequence);
        var set = _devices.GetOrAdd(userId, _ => new DeviceSet());
        lock (set.Sync)
        {
            // Повторная регистрация того же подключения сохраняет проекты, инструменты и режим.
            if (set.ByConnection.TryGetValue(connectionId, out var previous))
            {
                set.ByConnection[connectionId] = previous with
                {
                    Name = Clean(name, 100),
                    Os = Clean(os, 80),
                    Workspace = Clean(workspace, 260),
                    Seq = seq,
                };
            }
            else
            {
                // ОС и путь попадают в промпт, поэтому чистим: одна строка без управляющих символов, ограниченная длина.
                set.ByConnection[connectionId] = new Device(connectionId, Clean(name, 100), Clean(os, 80), Clean(workspace, 260), seq);
            }
            set.LastSeenAt = DateTime.UtcNow;
            set.RecomputeActive();
        }
        _logger.LogInformation("Desktop device registered: user={UserId} connection={ConnectionId} name={Name}", userId, connectionId, name);
    }

    public void Unregister(string connectionId)
    {
        // Одно подключение могло умереть, пока другие (демон или другое окно) живы: терять ПК нельзя.
        foreach (var (userId, set) in _devices)
        {
            lock (set.Sync)
            {
                if (!set.ByConnection.Remove(connectionId))
                    continue;
                set.LastSeenAt = DateTime.UtcNow;
                set.RecomputeActive();
            }
        }

        // Пустое множество ещё несколько минут нужно: по нему видно, что ПК отключился только что
        // (ExecuteAsync ждёт переподключения). Совсем старые записи убираем.
        foreach (var (userId, set) in _devices)
        {
            if (DateTime.UtcNow - set.LastSeenAt > TimeSpan.FromMinutes(10) && set.ByConnection.Count == 0)
                _devices.TryRemove(new KeyValuePair<Guid, DeviceSet>(userId, set));
        }

        foreach (var (callId, pending) in _pending)
        {
            if (pending.ConnectionId == connectionId && _pending.TryRemove(callId, out _))
                pending.Completion.TrySetResult(new DeviceToolOutcome(
                    "Связь с ПК оборвалась до завершения инструмента. НЕ повторяй вызов — сообщи пользователю и остановись.", true));
        }
    }

    public async Task<DeviceToolOutcome> ExecuteAsync(
        Guid userId, Guid taskId, string tool, string argumentsJson, CancellationToken ct,
        string? projectId = null, bool remoteApprovals = false)
    {
        // Соединение могло моргнуть: несколько секунд ждём повторной регистрации, чтобы задача не падала зря.
        var device0 = Active(userId);
        if (device0 is null && RecentlySeen(userId))
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (device0 is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
                device0 = Active(userId);
            }
        }
        if (device0 is not { } device)
            return new DeviceToolOutcome(
                "Связь с ПК потеряна: ConexyAI Desktop сейчас не подключён к серверу. " +
                "НЕ повторяй вызов и не пробуй другие инструменты ПК — сразу сообщи пользователю, " +
                "что нужно открыть клиент заново (или проверить интернет) и повторить задачу.", true);

        var callId = Guid.NewGuid();
        var completion = new TaskCompletionSource<DeviceToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[callId] = new Pending(device.ConnectionId, completion);

        try
        {
            // ORCHESTRA: чей это вызов (помощника) — отдельным событием ДО ExecuteTool (порядок в одном подключении
            // сохраняется), а не пятым аргументом: старые клиенты с 4-аргументным обработчиком не сломаются.
            if (AgentExecutionContext.Current is { } agent && agent.Id != AgentIdentity.Lead.Id)
                await _hub.Clients.Client(device.ConnectionId).SendAsync("ToolAgent", callId, agent.Id, agent.Role, ct);
            // WEB_DEVICE: добавлено 2026-10-18 — тем же приёмом: в каком проекте ПК выполнять и что подтверждения
            // этого вызова идут через веб.
            if (!string.IsNullOrWhiteSpace(projectId))
                await _hub.Clients.Client(device.ConnectionId).SendAsync("ToolProject", callId, projectId, ct);
            if (remoteApprovals)
                await _hub.Clients.Client(device.ConnectionId).SendAsync("ToolRemote", callId, ct);
            await _hub.Clients.Client(device.ConnectionId).SendAsync("ExecuteTool", callId, taskId, tool, argumentsJson, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallTimeout);
            await using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            return await completion.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeviceToolOutcome($"Устройство не ответило за {CallTimeout.TotalMinutes:0} мин.", true);
        }
        finally
        {
            _pending.TryRemove(callId, out _);
        }
    }

    public bool Complete(string connectionId, Guid callId, string output, bool isError)
    {
        if (!_pending.TryGetValue(callId, out var pending) || pending.ConnectionId != connectionId)
            return false;

        if (output.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            if (output.Length > MaxImageChars)
                output = "Скриншот слишком большой. Уменьши размер окна (width/height).";
        }
        // LOCAL_MCP: байты документа для read_document_file — обрезать нельзя, иначе файл не разберётся.
        else if (output.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            if (output.Length > MaxImageChars + FilePrefix.Length + 2_000_000)
                output = "Файл слишком большой для чтения (макс. 10 МБ).";
        }
        else if (output.Length > MaxOutputChars)
            output = output[..MaxOutputChars] + "\n[... вывод обрезан сервером ...]";

        return pending.Completion.TrySetResult(new DeviceToolOutcome(output, isError));
    }
}

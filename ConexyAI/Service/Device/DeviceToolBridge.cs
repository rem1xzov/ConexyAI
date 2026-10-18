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

    /// <summary>Регистрирует подключение как устройство пользователя (последнее подключение побеждает).</summary>
    void Register(Guid userId, string connectionId, string deviceName, string? os = null, string? workspace = null);

    /// <summary>Снимает подключение при разрыве; ждущие вызовы этого устройства завершаются ошибкой.</summary>
    void Unregister(string connectionId);

    // WEB_DEVICE: добавлено 2026-10-18
    /// <summary>Список проектов устройства (JSON-массив <c>[{id,name,path?}]</c>). False — подключение не устройство пользователя.</summary>
    bool RegisterProjects(Guid userId, string connectionId, string? projectsJson);

    /// <summary>Является ли подключение зарегистрированным устройством пользователя.</summary>
    bool IsDeviceConnection(Guid userId, string connectionId);

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

    private sealed record Device(string ConnectionId, string Name, string Os, string Workspace)
    {
        public IReadOnlyList<DeviceProject> Projects { get; init; } = Array.Empty<DeviceProject>();
    }

    // WEB_DEVICE: пределы списка проектов — он попадает в промпт и в ответ /device/status.
    public const int MaxProjects = 50;
    private const int MaxProjectNameChars = 100;

    private sealed record Pending(string ConnectionId, TaskCompletionSource<DeviceToolOutcome> Completion);

    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<DeviceToolBridge> _logger;
    private readonly ConcurrentDictionary<Guid, Device> _devices = new();
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    public DeviceToolBridge(IHubContext<ConexyHub> hub, ILogger<DeviceToolBridge> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public bool IsOnline(Guid userId) => _devices.ContainsKey(userId);

    public DeviceInfo? GetInfo(Guid userId) =>
        _devices.TryGetValue(userId, out var d) ? new DeviceInfo(d.Name, d.Os, d.Workspace, d.Projects) : null;

    public bool IsDeviceConnection(Guid userId, string connectionId) =>
        _devices.TryGetValue(userId, out var d) && d.ConnectionId == connectionId;

    public bool RegisterProjects(Guid userId, string connectionId, string? projectsJson)
    {
        if (!_devices.TryGetValue(userId, out var device) || device.ConnectionId != connectionId)
            return false;

        var projects = ParseProjects(projectsJson);
        _devices.TryUpdate(userId, device with { Projects = projects }, device);
        _logger.LogInformation("Desktop device projects: user={UserId} count={Count}", userId, projects.Count);
        return true;
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

    public void Register(Guid userId, string connectionId, string deviceName, string? os = null, string? workspace = null)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim();
        // ОС и путь попадают в промпт, поэтому чистим: одна строка, без управляющих символов, ограниченная длина.
        // WEB_DEVICE: повторная регистрация того же подключения сохраняет присланный ранее список проектов.
        var projects = _devices.TryGetValue(userId, out var previous) && previous.ConnectionId == connectionId
            ? previous.Projects
            : Array.Empty<DeviceProject>();
        _devices[userId] = new Device(connectionId, Clean(name, 100), Clean(os, 80), Clean(workspace, 260)) { Projects = projects };
        _logger.LogInformation("Desktop device registered: user={UserId} connection={ConnectionId} name={Name}", userId, connectionId, name);
    }

    public void Unregister(string connectionId)
    {
        foreach (var (userId, device) in _devices)
        {
            if (device.ConnectionId == connectionId)
                _devices.TryRemove(new KeyValuePair<Guid, Device>(userId, device));
        }

        foreach (var (callId, pending) in _pending)
        {
            if (pending.ConnectionId == connectionId && _pending.TryRemove(callId, out _))
                pending.Completion.TrySetResult(new DeviceToolOutcome("Устройство отключилось до завершения инструмента.", true));
        }
    }

    public async Task<DeviceToolOutcome> ExecuteAsync(
        Guid userId, Guid taskId, string tool, string argumentsJson, CancellationToken ct,
        string? projectId = null, bool remoteApprovals = false)
    {
        if (!_devices.TryGetValue(userId, out var device))
            return new DeviceToolOutcome(
                "Десктопное приложение не подключено. Запустите ConexyAI Desktop и повторите.", true);

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

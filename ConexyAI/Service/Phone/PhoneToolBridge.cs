using System.Collections.Concurrent;
using System.Text.Json;
using ConexyAI.Hub;
using ConexyAI.Service.Device;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Phone;

// PHONE_AGENT: добавлено 2026-10-29 — исполнение инструментов агента на телефоне пользователя.
// Это отдельный мост рядом с DeviceToolBridge: телефон и ПК не вытесняют друг друга. Сервер ведёт
// агентский цикл, а phone_* выполняет нативный плагин приложения: событие PhoneTool → PhoneToolResult.

/// <summary>Сведения о подключённом телефоне — для промпта агента и подписи в UI.</summary>
public sealed record PhoneInfo(
    string Model,
    string Manufacturer,
    string AndroidVersion,
    int Sdk,
    string AppVersion,
    IReadOnlyDictionary<string, string> Permissions);

public interface IPhoneToolBridge
{
    /// <summary>Регистрирует подключение как телефон пользователя (повторяется после каждого переподключения).</summary>
    bool Register(Guid userId, string connectionId, string? infoJson);

    /// <summary>Приложение на экране (true) или свёрнуто. Только для зарегистрированного подключения.</summary>
    bool SetForeground(Guid userId, string connectionId, bool foreground);

    /// <summary>Подключён ли телефон пользователя.</summary>
    bool IsOnline(Guid userId);

    /// <summary>Телефон подключён И приложение на экране. Офлайн — всегда false.</summary>
    bool IsForeground(Guid userId);

    bool IsPhoneConnection(Guid userId, string connectionId);

    PhoneInfo? GetInfo(Guid userId);

    /// <summary>Ждёт регистрации телефона до <paramref name="timeout"/>. True — телефон на связи.</summary>
    Task<bool> WaitForOnlineAsync(Guid userId, TimeSpan timeout, CancellationToken ct);

    Task<DeviceToolOutcome> ExecuteAsync(Guid userId, Guid taskId, string tool, string argumentsJson, CancellationToken ct);

    /// <summary>Принимает результат; чужое подключение результат принять не может.</summary>
    bool Complete(string connectionId, Guid callId, string output, bool isError);

    void Unregister(string connectionId);
}

public sealed class PhoneToolBridge : IPhoneToolBridge
{
    /// <summary>Начало текста ошибки, когда связь с телефоном потеряна (по нему агент не повторяет вызовы).</summary>
    public const string OfflinePrefix = "Связь с телефоном";

    public const string OfflineMessage =
        "Связь с телефоном потеряна: приложение ConexyAI сейчас не подключено к серверу. " +
        "НЕ повторяй вызов и не пробуй другие инструменты телефона — сразу сообщи пользователю, " +
        "что нужно открыть приложение на телефоне (и проверить интернет) и повторить задачу.";

    private const int MaxOutputChars = 200_000;
    private const int MaxImageChars = 12_000_000;

    private sealed record Phone(string ConnectionId, PhoneInfo Info, long Seq, bool Foreground);

    private sealed class PhoneSet
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, Phone> ByConnection = new(StringComparer.Ordinal);
        public DateTime LastSeenAt = DateTime.UtcNow;

        public Phone? Active()
        {
            Phone? best = null;
            foreach (var p in ByConnection.Values)
                if (best is null || p.Seq > best.Seq)
                    best = p;
            return best;
        }
    }

    private sealed record Pending(string ConnectionId, TaskCompletionSource<DeviceToolOutcome> Completion);

    private readonly IHubContext<ConexyHub> _hub;
    private readonly ILogger<PhoneToolBridge> _logger;
    private readonly Func<string, TimeSpan> _timeoutFor;
    private readonly ConcurrentDictionary<Guid, PhoneSet> _phones = new();
    private readonly ConcurrentDictionary<Guid, List<TaskCompletionSource>> _waiters = new();
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private long _sequence;

    public PhoneToolBridge(IHubContext<ConexyHub> hub, ILogger<PhoneToolBridge> logger, Func<string, TimeSpan>? timeoutFor = null)
    {
        _hub = hub;
        _logger = logger;
        _timeoutFor = timeoutFor ?? PhoneToolCatalog.TimeoutFor;
    }

    private Phone? Active(Guid userId)
    {
        if (!_phones.TryGetValue(userId, out var set))
            return null;
        lock (set.Sync)
            return set.Active();
    }

    public bool IsOnline(Guid userId) => Active(userId) is not null;

    public bool IsForeground(Guid userId) => Active(userId)?.Foreground == true;

    public bool IsPhoneConnection(Guid userId, string connectionId) =>
        _phones.TryGetValue(userId, out var set) && set.ByConnection.ContainsKey(connectionId);

    public PhoneInfo? GetInfo(Guid userId) => Active(userId)?.Info;

    private bool RecentlySeen(Guid userId) =>
        _phones.TryGetValue(userId, out var set) && DateTime.UtcNow - set.LastSeenAt < TimeSpan.FromSeconds(30);

    public bool Register(Guid userId, string connectionId, string? infoJson)
    {
        var info = ParseInfo(infoJson);
        var seq = Interlocked.Increment(ref _sequence);
        var set = _phones.GetOrAdd(userId, _ => new PhoneSet());
        lock (set.Sync)
        {
            var foreground = set.ByConnection.TryGetValue(connectionId, out var previous) && previous.Foreground;
            set.ByConnection[connectionId] = new Phone(connectionId, info, seq, foreground);
            set.LastSeenAt = DateTime.UtcNow;
        }

        _logger.LogInformation("Phone registered: user={UserId} connection={ConnectionId} model={Model}", userId, connectionId, info.Model);

        if (_waiters.TryRemove(userId, out var waiters))
        {
            lock (waiters)
                foreach (var w in waiters)
                    w.TrySetResult();
        }
        return true;
    }

    public bool SetForeground(Guid userId, string connectionId, bool foreground)
    {
        if (!_phones.TryGetValue(userId, out var set))
            return false;
        lock (set.Sync)
        {
            if (!set.ByConnection.TryGetValue(connectionId, out var phone))
                return false;
            set.ByConnection[connectionId] = phone with { Foreground = foreground };
            set.LastSeenAt = DateTime.UtcNow;
            return true;
        }
    }

    public async Task<bool> WaitForOnlineAsync(Guid userId, TimeSpan timeout, CancellationToken ct)
    {
        if (IsOnline(userId))
            return true;
        if (timeout <= TimeSpan.Zero)
            return false;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _waiters.GetOrAdd(userId, _ => new List<TaskCompletionSource>());
        lock (list)
            list.Add(tcs);

        // Телефон мог зарегистрироваться между проверкой и добавлением ожидания.
        if (IsOnline(userId))
        {
            lock (list)
                list.Remove(tcs);
            return true;
        }

        try
        {
            await tcs.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return IsOnline(userId);
        }
        finally
        {
            lock (list)
                list.Remove(tcs);
        }
    }

    public void Unregister(string connectionId)
    {
        foreach (var (_, set) in _phones)
        {
            lock (set.Sync)
            {
                if (set.ByConnection.Remove(connectionId))
                    set.LastSeenAt = DateTime.UtcNow;
            }
        }

        foreach (var (userId, set) in _phones)
        {
            lock (set.Sync)
            {
                if (set.ByConnection.Count == 0 && DateTime.UtcNow - set.LastSeenAt > TimeSpan.FromMinutes(10))
                    _phones.TryRemove(new KeyValuePair<Guid, PhoneSet>(userId, set));
            }
        }

        foreach (var (callId, pending) in _pending)
        {
            if (pending.ConnectionId == connectionId && _pending.TryRemove(callId, out _))
                pending.Completion.TrySetResult(new DeviceToolOutcome(OfflineMessage, true));
        }
    }

    public async Task<DeviceToolOutcome> ExecuteAsync(
        Guid userId, Guid taskId, string tool, string argumentsJson, CancellationToken ct)
    {
        if (!PhoneToolCatalog.TryNormalizeArguments(tool, argumentsJson, out var arguments, out var argError))
            return new DeviceToolOutcome(argError ?? "Некорректные аргументы.", true);

        // Соединение могло моргнуть: несколько секунд ждём повторной регистрации.
        var phone0 = Active(userId);
        if (phone0 is null && RecentlySeen(userId))
        {
            await WaitForOnlineAsync(userId, TimeSpan.FromSeconds(5), ct);
            phone0 = Active(userId);
        }
        if (phone0 is not { } phone)
            return new DeviceToolOutcome(OfflineMessage, true);

        var callId = Guid.NewGuid();
        var completion = new TaskCompletionSource<DeviceToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[callId] = new Pending(phone.ConnectionId, completion);
        var timeoutSpan = _timeoutFor(tool);

        try
        {
            await _hub.Clients.Client(phone.ConnectionId).SendAsync("PhoneTool", new
            {
                callId = callId.ToString(),
                taskId = taskId.ToString(),
                tool,
                arguments,
            }, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutSpan);
            await using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
            return await completion.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeviceToolOutcome(
                $"Телефон не ответил за {timeoutSpan.TotalMinutes:0} мин. Сообщи пользователю и не повторяй вызов без его просьбы.", true);
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

        output ??= string.Empty;
        if (output.StartsWith(DeviceToolBridge.ImagePrefix, StringComparison.Ordinal))
        {
            if (output.Length > MaxImageChars)
            {
                output = "Изображение слишком большое для передачи. Выбери другое или уменьши размер.";
                isError = true;
            }
        }
        else if (output.Length > MaxOutputChars)
            output = output[..MaxOutputChars] + "\n[... вывод обрезан сервером ...]";

        return pending.Completion.TrySetResult(new DeviceToolOutcome(output, isError));
    }

    /// <summary>Клиенту не доверяем: короткие строки без управляющих символов, только известные разрешения и состояния.</summary>
    public static PhoneInfo ParseInfo(string? json)
    {
        string model = string.Empty, manufacturer = string.Empty, android = string.Empty, app = string.Empty;
        var sdk = 0;
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(json) && json.Length <= 64_000)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    model = Clean(Str(root, "model"), 100);
                    manufacturer = Clean(Str(root, "manufacturer"), 60);
                    android = Clean(Str(root, "androidVersion"), 20);
                    app = Clean(Str(root, "appVersion"), 30);
                    if (root.TryGetProperty("sdk", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) && n is > 0 and < 1000)
                        sdk = n;
                    if (root.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in p.EnumerateObject())
                        {
                            if (Array.IndexOf(PhoneToolCatalog.Permissions, prop.Name) < 0 || prop.Value.ValueKind != JsonValueKind.String)
                                continue;
                            var state = prop.Value.GetString();
                            if (state is "granted" or "denied" or "prompt" or "unavailable")
                                permissions[prop.Name] = state;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Битый JSON — телефон без подробностей.
            }
        }
        return new PhoneInfo(model, manufacturer, android, sdk, app, permissions);

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string Clean(string? v, int max) =>
        new string((v ?? string.Empty).Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();
}

using System.Collections.Concurrent;
using System.Text.Json;

namespace ConexyAI.Service;

// AGENT_CURSOR: добавлено 2026-11-19 — координируемые курсоры. Несколько агентов работают на одном
// экране, поэтому (а) сервер помнит, где действует каждый, и рассылает это web/TUI и оверлею на ПК,
// (б) ввод на ПК сериализуется: физическая мышь одна, и два агента не должны кликать одновременно.
/// <summary>Last known screen action of one agent (for the overlay, the web cards and screenshot legends).</summary>
public sealed record AgentCursorState(
    string TaskId,
    string AgentId,
    string AgentRole,
    string Color,
    string Tool,
    int? X,
    int? Y,
    int? ToX,
    int? ToY,
    bool? Changed,
    DateTime At);

/// <summary>Stable per-agent colors shared by the server, the TUI and the web cards.</summary>
public static class AgentCursorColors
{
    /// <summary>The lead has its own fixed color; helpers get a stable palette slot.</summary>
    public const string Lead = "#FF8787";

    private static readonly string[] Palette = ["#5FD7AF", "#FF87D7", "#87D787", "#FFAF5F", "#AF87FF"];

    /// <summary>Color of an agent: derived from the id so every surface shows the same one.</summary>
    public static string For(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId) || agentId == "lead")
            return Lead;

        // FNV-1a: стабильно между процессами и языками, без рандома.
        var hash = 2166136261u;
        foreach (var ch in agentId)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return Palette[hash % (uint)Palette.Length];
    }
}

/// <summary>Where each agent of a run last acted on the screen.</summary>
public sealed class ComputerCursorRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, AgentCursorState>> _byTask = new();

    public void Update(AgentCursorState state)
    {
        var agents = _byTask.GetOrAdd(Guid.Parse(state.TaskId), _ => new ConcurrentDictionary<string, AgentCursorState>(StringComparer.Ordinal));
        agents[state.AgentId] = state;
    }

    /// <summary>Current cursor of every agent of the run (lead first, then helpers by role).</summary>
    public IReadOnlyList<AgentCursorState> Snapshot(Guid taskId) =>
        _byTask.TryGetValue(taskId, out var agents)
            ? agents.Values
                .OrderBy(state => state.AgentId == "lead" ? 0 : 1)
                .ThenBy(state => state.AgentRole, StringComparer.Ordinal)
                .ToList()
            : [];

    public void Clear(Guid taskId) => _byTask.TryRemove(taskId, out _);
}

/// <summary>
/// Один экран — один активный ввод. Аренда выдаётся на время действия и возвращается сразу после
/// ответа ПК; TTL страхует от падения прогона. Второй агент получает честный отказ с именем владельца.
/// </summary>
public sealed class ComputerInputLease
{
    /// <summary>Сколько живёт аренда, если прогон упал между acquire и release.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private sealed record Holder(string AgentId, string Role, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<Guid, Holder> _holders = new();
    private readonly TimeSpan _ttl;

    public ComputerInputLease(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Захватывает ввод для агента. Возвращает <c>false</c>, когда экран занят ДРУГИМ агентом
    /// (его роль — в <paramref name="ownerRole"/>). Своя повторная аренда продлевается.
    /// </summary>
    public bool TryAcquire(Guid userId, string agentId, string agentRole, out string ownerRole)
    {
        ownerRole = string.Empty;
        var now = DateTime.UtcNow;

        while (true)
        {
            if (_holders.TryGetValue(userId, out var current))
            {
                if (current.ExpiresAt > now && current.AgentId != agentId)
                {
                    ownerRole = current.Role;
                    return false;
                }

                if (_holders.TryUpdate(userId, new Holder(agentId, agentRole, now + _ttl), current))
                    return true;
                continue;
            }

            if (_holders.TryAdd(userId, new Holder(agentId, agentRole, now + _ttl)))
                return true;
        }
    }

    /// <summary>Возвращает ввод только если держателем остаётся этот агент.</summary>
    public void Release(Guid userId, string agentId)
    {
        if (_holders.TryGetValue(userId, out var current) && current.AgentId == agentId)
            _holders.TryRemove(new KeyValuePair<Guid, Holder>(userId, current));
    }

    /// <summary>Роль текущего держателя ввода, если аренда ещё жива.</summary>
    public string? OwnerRole(Guid userId) =>
        _holders.TryGetValue(userId, out var current) && current.ExpiresAt > DateTime.UtcNow ? current.Role : null;
}

/// <summary>Координаты действия из аргументов computer-инструмента (снимок/цель перетаскивания).</summary>
public static class CursorArgs
{
    public static (int? X, int? Y, int? ToX, int? ToY) Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return (null, null, null, null);

        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null, null);

            int? Read(string name) =>
                root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
                    ? value
                    : null;

            return (Read("x"), Read("y"), Read("to_x"), Read("to_y"));
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }
}

using System.Text.Json;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// AGENT_HOOKS: добавлено 2026-10-04 — хуки проекта. Пользователь кладёт в корень рабочей области
// .conexy/hooks.json, и его команды запускаются на события агента (before_task, after_file_change,
// on_error, after_task). Это НЕ git-хуки: git-хуки в песочнице намеренно выключены (защита), а здесь
// команды пользователя выполняются в изолированной песочнице как автоматизация.
public sealed record AgentHooks(IReadOnlyDictionary<string, IReadOnlyList<string>> Events);

public interface IAgentHooksService
{
    /// <summary>Loads <c>.conexy/hooks.json</c> for the chat workspace, or <c>null</c> when there are no hooks.</summary>
    Task<AgentHooks?> LoadAsync(Guid chatId, CancellationToken ct = default);

    IReadOnlyList<string> CommandsFor(AgentHooks hooks, string eventName);

    /// <summary>Replaces <c>{name}</c> placeholders with shell-quoted values.</summary>
    string Substitute(string command, IReadOnlyDictionary<string, string> values);
}

public class ConexyAgentHooksService : IAgentHooksService
{
    public const string HooksFile = ".conexy/hooks.json";
    public static readonly string[] KnownEvents = { "before_task", "after_file_change", "on_error", "after_task" };
    public const int MaxHooksPerEvent = 10;
    public const int MaxCommandLength = 2000;

    private readonly IConexyWorkspaceService _workspace;

    public ConexyAgentHooksService(IConexyWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public async Task<AgentHooks?> LoadAsync(Guid chatId, CancellationToken ct = default)
    {
        // No workspace yet means no hooks file (and reading would create an empty directory).
        if (_workspace.GetTaskWorkspacePathIfExists(chatId) is null)
            return null;

        FileReadResult read;
        try
        {
            read = await _workspace.ReadFileAsync(chatId, HooksFile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (!read.Success || string.IsNullOrWhiteSpace(read.Content))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(read.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // Accept both { "before_task": [...] } and { "hooks": { "before_task": [...] } }.
            if (root.TryGetProperty("hooks", out var nested) && nested.ValueKind == JsonValueKind.Object)
                root = nested;

            var events = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (!KnownEvents.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                var commands = ReadCommands(property.Value);
                if (commands.Count > 0)
                    events[property.Name] = commands;
            }

            return events.Count == 0 ? null : new AgentHooks(events);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> CommandsFor(AgentHooks hooks, string eventName) =>
        hooks.Events.TryGetValue(eventName, out var commands) ? commands : Array.Empty<string>();

    public string Substitute(string command, IReadOnlyDictionary<string, string> values)
    {
        var result = command;
        foreach (var (key, value) in values)
            result = result.Replace("{" + key + "}", ShellQuote(value), StringComparison.Ordinal);
        return result;
    }

    private static IReadOnlyList<string> ReadCommands(JsonElement element)
    {
        var list = new List<string>();

        if (element.ValueKind == JsonValueKind.String)
        {
            var single = element.GetString();
            if (!string.IsNullOrWhiteSpace(single) && single.Length <= MaxCommandLength)
                list.Add(single.Trim());
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var command = item.GetString();
                if (string.IsNullOrWhiteSpace(command) || command.Length > MaxCommandLength) continue;
                list.Add(command.Trim());
                if (list.Count >= MaxHooksPerEvent) break;
            }
        }

        return list;
    }

    // Single-quote the value so a path with spaces is one argument; embedded quotes are escaped.
    private static string ShellQuote(string value) => "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";
}

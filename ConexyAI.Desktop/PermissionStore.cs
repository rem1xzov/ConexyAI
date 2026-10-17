using System.Text.Json;
using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>
/// Постоянные разрешения команд по проекту («всегда разрешать npm test здесь»), как allowlist в Claude Code.
/// Правило — начало команды (1–2 слова). Подходит только одиночная команда без цепочек и перенаправлений:
/// «npm test; rm -rf …» правилом «npm test» не пройдёт. Запрещённые политикой команды не разрешаются никогда.
/// </summary>
public sealed class PermissionStore
{
    private static readonly StringComparer PathCmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Dictionary<string, List<string>> Projects { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI", "permissions.json");

    public static PermissionStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var store = JsonSerializer.Deserialize<PermissionStore>(File.ReadAllText(FilePath)) ?? new PermissionStore();
                store.Projects = new Dictionary<string, List<string>>(store.Projects, PathCmp);
                return store;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new PermissionStore { Projects = new Dictionary<string, List<string>>(PathCmp) };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Key(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public IReadOnlyList<string> RulesFor(string root) =>
        Projects.TryGetValue(Key(root), out var rules) ? rules : [];

    /// <summary>Правило, которое предложим запомнить для этой команды, или null (составная/опасная команда).</summary>
    public static string? SuggestRule(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0 || CommandPolicy.IsCompound(trimmed) || CommandPolicy.ForbiddenReason(trimmed) is not null)
            return null;
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // «npm test», «dotnet build», «./gradlew build», «git push»; одно слово, если дальше идут флаги или пути.
        return words.Length >= 2 && !words[1].StartsWith('-') && !words[1].Contains('/') && !words[1].Contains('\\') && words[1].Length <= 30
            ? $"{words[0]} {words[1]}"
            : words[0];
    }

    public string? Match(string root, string command)
    {
        var trimmed = command.Trim();
        if (CommandPolicy.IsCompound(trimmed) || CommandPolicy.ForbiddenReason(trimmed) is not null)
            return null;
        return RulesFor(root).FirstOrDefault(rule =>
            trimmed.Equals(rule, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(rule + " ", StringComparison.OrdinalIgnoreCase));
    }

    public void Add(string root, string rule)
    {
        var key = Key(root);
        if (!Projects.TryGetValue(key, out var rules))
            Projects[key] = rules = [];
        if (!rules.Contains(rule, StringComparer.OrdinalIgnoreCase))
            rules.Add(rule);
        Save();
    }

    public bool Remove(string root, int index)
    {
        if (!Projects.TryGetValue(Key(root), out var rules) || index < 0 || index >= rules.Count)
            return false;
        rules.RemoveAt(index);
        Save();
        return true;
    }

    public void Clear(string root)
    {
        Projects.Remove(Key(root));
        Save();
    }
}

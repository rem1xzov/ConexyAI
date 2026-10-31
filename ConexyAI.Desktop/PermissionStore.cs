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

    // PERMISSION_RULES_STRICT: интерпретаторы и оболочки выполняют произвольный код из аргументов
    // (`python -c …`, `node -e …`, `pwsh -Command …`) — постоянное правило для них равно «разрешить всё».
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "python2", "py", "pythonw", "node", "nodejs", "deno", "bun", "bunx", "powershell", "pwsh", "cmd",
        "bash", "sh", "zsh", "fish", "dash", "ksh", "wsl", "ruby", "irb", "perl", "php", "lua", "java", "jshell", "npx", "pnpx",
        "uvx", "uv", "pipx", "dotnet", "env", "sudo", "start", "start-process", "invoke-expression", "iex", "invoke-command",
        "cscript", "wscript", "mshta", "rundll32", "regsvr32", "osascript", "xargs", "eval", "exec", "nohup", "time", "watch",
        "ssh", "scp", "curl", "wget", "iwr", "invoke-webrequest", "irm", "invoke-restmethod",
    };

    // «dotnet» целиком — интерпретатор (`dotnet x.dll`), но «dotnet build/test» — обычное правило.
    private static readonly HashSet<string> DotnetVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "test", "restore", "format", "publish", "pack", "clean", "run", "watch",
    };

    private static string Exe(string word) =>
        Path.GetFileNameWithoutExtension(word.Replace('\\', '/').Split('/')[^1]);

    /// <summary>Правило, которое предложим запомнить для этой команды, или null (составная/опасная команда).</summary>
    public static string? SuggestRule(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0 || CommandPolicy.IsCompound(trimmed) || CommandPolicy.ForbiddenReason(trimmed) is not null)
            return null;
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // «npm test», «dotnet build», «./gradlew build», «git push». Одного слова мало: «python» или «npm»
        // разрешили бы любой код — если второе слово флаг/путь, правило не предлагаем.
        if (words.Length < 2 || words[1].StartsWith('-') || words[1].Contains('/') || words[1].Contains('\\') || words[1].Length > 30)
            return null;
        var head = Exe(words[0]);
        if (Interpreters.Contains(head) && !(head.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && DotnetVerbs.Contains(words[1])))
            return null;
        // «npm exec», «pnpm dlx», «yarn dlx» — то же npx: скачать и запустить любой пакет.
        if (words[1] is "exec" or "x" or "dlx" or "eval")
            return null;
        return $"{words[0]} {words[1]}";
    }

    /// <summary>Правило из файла годится (старые однословные «python»/«npm» и правила для интерпретаторов игнорируются).</summary>
    private static bool IsValidRule(string rule) => SuggestRule(rule) is not null;

    public string? Match(string root, string command)
    {
        var trimmed = command.Trim();
        if (CommandPolicy.IsCompound(trimmed) || CommandPolicy.ForbiddenReason(trimmed) is not null)
            return null;
        return RulesFor(root).FirstOrDefault(rule =>
            IsValidRule(rule)
            && (trimmed.Equals(rule, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(rule + " ", StringComparison.OrdinalIgnoreCase)));
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

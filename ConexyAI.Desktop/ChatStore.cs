using System.Text.Json;

namespace ConexyAI.Desktop;

/// <summary>
/// Локальные привязки «чат → папка проекта». Сам список чатов и история живут на сервере (общие с сайтом),
/// а папка на ПК — только здесь: сервер не знает, где у вас лежит проект.
/// </summary>
public sealed class ChatStore
{
    public Dictionary<Guid, string> Folders { get; set; } = new();

    public Dictionary<Guid, DateTime> LastUsed { get; set; } = new();

    /// <summary>Чаты, в которых включён оркестр агентов: он работает, пока пользователь не выключит его сам.</summary>
    public HashSet<Guid> Team { get; set; } = new();

    public void SetTeam(Guid chatId, bool on)
    {
        if (on ? Team.Add(chatId) : Team.Remove(chatId))
            Save();
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI", "chats.json");

    public static ChatStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ChatStore>(File.ReadAllText(FilePath)) ?? new ChatStore();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Битый файл — начинаем с чистого листа: чаты всё равно приходят с сервера.
        }
        return new ChatStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? FolderOf(Guid chatId) =>
        Folders.TryGetValue(chatId, out var f) && Directory.Exists(f) ? f : null;

    public void Bind(Guid chatId, string folder)
    {
        Folders[chatId] = folder;
        Touch(chatId);
    }

    public void Touch(Guid chatId)
    {
        LastUsed[chatId] = DateTime.UtcNow;
        Save();
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Последний использованный чат этой папки или <paramref name="fallback"/> (чат «по умолчанию»).</summary>
    public Guid ChatFor(string folder, Guid fallback)
    {
        var best = Folders
            .Where(p => SameFolder(p.Value, folder))
            .OrderByDescending(p => LastUsed.GetValueOrDefault(p.Key))
            .Select(p => (Guid?)p.Key)
            .FirstOrDefault();
        return best ?? fallback;
    }
}

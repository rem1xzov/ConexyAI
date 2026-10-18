using System.Text.Json;

namespace ConexyAI.Desktop;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>Проект на этом ПК, в котором сайт может запускать задачи. Id стабилен — по нему сервер маршрутизирует вызовы.</summary>
public sealed class DeviceProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>Список проектов (conexy projects …) в projects.json рядом с config.json.</summary>
public sealed class ProjectStore
{
    public List<DeviceProject> Projects { get; set; } = [];

    public static string ConfigDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI");

    private static string FilePath => System.IO.Path.Combine(ConfigDir, "projects.json");

    public static ProjectStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ProjectStore>(File.ReadAllText(FilePath)) ?? new ProjectStore();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new ProjectStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public DeviceProject? Find(string idOrName) =>
        Projects.FirstOrDefault(p => p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? Projects.FirstOrDefault(p => p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Проект для папки: найденный или новый (сохраняется сразу). Added — был ли он добавлен сейчас.</summary>
    public (DeviceProject Project, bool Added) Ensure(string folder)
    {
        var path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Projects.FirstOrDefault(p => p.Path.Equals(path, cmp)) is { } existing)
            return (existing, false);
        var project = new DeviceProject { Name = System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path, Path = path };
        Projects.Add(project);
        Save();
        return (project, true);
    }

    /// <summary>JSON для хаба RegisterDeviceProjects: [{id,name,path}].</summary>
    public string ToJson() => JsonSerializer.Serialize(
        Projects.Select(p => new { id = p.Id, name = p.Name, path = p.Path }),
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

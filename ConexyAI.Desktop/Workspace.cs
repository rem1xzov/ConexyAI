namespace ConexyAI.Desktop;

// WORKSPACE_GUARD: добавлено 2026-10-18 — агент один раз «осматривался» в C:\WINDOWS\system32 и
// получил дамп системных DLL. Такая папка не может быть рабочей: там нет проекта, туда нельзя писать
// вложения (фото из сообщения просто терялось), и запросы вроде «покажи фото» уходили в пустоту.
public static class Workspace
{
    /// <summary>Системные папки, которые никогда не становятся рабочей областью.</summary>
    private static IEnumerable<string> ProtectedFolders()
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        if (Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } windows)
            yield return windows;
        if (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf)
            yield return pf;
        if (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) is { Length: > 0 } pfx86)
            yield return pfx86;
        if (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } common)
            yield return common;
    }

    /// <summary>Почему папка не годится как рабочая (null — годится).</summary>
    public static string? WhyUnsafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "путь не указан";
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "путь не разбирается";
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
            return "это корень диска";

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var folder in ProtectedFolders())
        {
            var protectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (full.Equals(protectedPath, cmp) || full.StartsWith(protectedPath + Path.DirectorySeparatorChar, cmp))
                return $"это системная папка ({protectedPath})";
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) &&
            full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), cmp))
            return "это корень профиля пользователя";

        return null;
    }

    public static bool IsUnsafe(string? path) => WhyUnsafe(path) is not null;

    /// <summary>Безопасное место для проекта, куда точно можно писать: source\repos или Документы.</summary>
    public static string Suggest()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var repos = Path.Combine(profile, "source", "repos");
        var target = Directory.Exists(repos) ? repos : Path.Combine(profile, "Documents", "ConexyAI");
        Directory.CreateDirectory(target);
        return target;
    }
}

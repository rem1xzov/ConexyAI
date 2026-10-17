namespace ConexyAI.Desktop.Tools;

/// <summary>Ошибка инструмента: текст уходит модели как есть.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// Граница рабочей папки. Любой путь от модели разрешается только внутри корня: «..», абсолютные
/// пути наружу и символические ссылки/junction, ведущие за пределы папки, отклоняются.
/// Сервер не считается доверенным — эту проверку клиент делает сам.
/// </summary>
public sealed class PathGuard
{
    private static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Root { get; }

    /// <summary>Снимок файла перед записью (для /rewind). Вызывается для каждого пути записи.</summary>
    public Action<string>? BeforeWrite { get; set; }

    public PathGuard(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Разрешает путь для чтения.</summary>
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("Путь не указан.");

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        if (!IsInside(full))
            throw new ToolException($"Путь '{path}' вне рабочей папки ({Root}). Доступ запрещён.");

        RejectEscapingLinks(full);
        return full;
    }

    /// <summary>Разрешает путь для записи: дополнительно запрещает трогать служебную папку .git.</summary>
    public string ResolveForWrite(string? path)
    {
        var full = Resolve(path);
        var relative = Path.GetRelativePath(Root, full);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(".git", Cmp)))
            throw new ToolException("Запись внутрь .git запрещена.");

        BeforeWrite?.Invoke(full);
        return full;
    }

    public string Relative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private bool IsInside(string full) =>
        full.Equals(Root, Cmp) || full.StartsWith(Root + Path.DirectorySeparatorChar, Cmp);

    private void RejectEscapingLinks(string full)
    {
        var current = Root;
        var relative = Path.GetRelativePath(Root, full);
        if (relative == ".")
            return;

        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
                return; // дальше ничего нет — ссылок тоже

            if (info.LinkTarget is null)
                continue;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !IsInside(Path.GetFullPath(target.FullName)))
                throw new ToolException($"'{Relative(current)}' — ссылка за пределы рабочей папки. Доступ запрещён.");
        }
    }
}

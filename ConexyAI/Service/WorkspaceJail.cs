using System.Text;

namespace ConexyAI.Service;

// WORKSPACE_JAIL: добавлено 2026-09-24 — ревью C2 (побег из воркспейса через симлинк).
//
// Старый джейл сравнивал только ТЕКСТ пути (GetFullPath + префикс корня), а файловые операции идут
// в процессе бэкенда (File.ReadAllText / WriteAllText) и следуют по симлинкам. Агент в песочнице
// делал `ln -s /proc/self/environ e`, затем `file_read e` — и получал JWT-ключ, пароль БД и API-ключи;
// через симлинк на каталог — запись куда угодно.
//
// Теперь:
//   1. путь канонизируется по-настоящему: каждый существующий компонент проверяется на симлинк, и
//      его цель подставляется (как realpath); проверяется, что РЕАЛЬНЫЙ путь лежит под РЕАЛЬНЫМ
//      корнем, и дальше открывается именно он;
//   2. после открытия (Linux) дескриптор перепроверяется через /proc/self/fd — так гонка «проверил,
//      а симлинк подменили до open» ловится на уже открытом файле, до чтения или записи;
//   3. запись открывает файл БЕЗ усечения, проверяет дескриптор и только потом обрезает — поэтому
//      подменённая на симлинк цель не обнуляется;
//   4. обход каталогов не заходит в симлинки (иначе `ln -s / host` отдаёт листинг хоста, а
//      `ln -s . loop` — бесконечную рекурсию).

/// <summary>Symlink-aware containment checks and verified file I/O for per-chat workspaces.</summary>
public static class WorkspaceJail
{
    private const int MaxSymlinkHops = 40;

    /// <summary>
    /// Enumeration that never follows or returns symbolic links (reported as reparse points on every
    /// platform .NET supports) and keeps dot-files, like the old SearchOption-based listing did.
    /// </summary>
    public static EnumerationOptions NoLinks(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against the workspace <paramref name="root"/> and returns
    /// the canonical path with every symlink resolved. Throws <see cref="UnauthorizedAccessException"/>
    /// when the lexical path or the real path escapes the root.
    /// </summary>
    public static string Resolve(string root, string? relativePath)
    {
        var realRoot = GetRealPath(root);
        var combined = Path.GetFullPath(Path.Combine(realRoot, relativePath ?? string.Empty));
        if (!IsInside(realRoot, combined))
            throw new UnauthorizedAccessException("Path traversal outside the workspace is not allowed.");

        var real = GetRealPath(combined);
        if (!IsInside(realRoot, real))
            throw new UnauthorizedAccessException("The path resolves (via a symbolic link) outside the workspace.");

        return real;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or lies below it.</summary>
    public static bool IsInside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Equals(normalizedRoot, comparison))
            return true;
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Canonical form of <paramref name="path"/>: absolute, with every existing symlink component
    /// replaced by its target (like POSIX realpath). A non-existing tail is appended as is — it cannot
    /// contain links.
    /// </summary>
    public static string GetRealPath(string path)
    {
        var current = Path.GetFullPath(path);
        var hops = 0;

        restart:
        var root = Path.GetPathRoot(current) ?? string.Empty;
        var segments = current[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var resolved = root;

        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(resolved, segments[i]);
            string? linkTarget;
            try
            {
                linkTarget = new FileInfo(candidate).LinkTarget;
            }
            catch (IOException)
            {
                linkTarget = null;
            }
            catch (UnauthorizedAccessException)
            {
                linkTarget = null;
            }

            if (linkTarget is null)
            {
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    // Nothing on disk from here on: the rest cannot be a link.
                    return Path.GetFullPath(Path.Combine(new[] { resolved }.Concat(segments[i..]).ToArray()));
                }

                resolved = candidate;
                continue;
            }

            if (++hops > MaxSymlinkHops)
                throw new IOException("Too many levels of symbolic links.");

            var target = Path.IsPathRooted(linkTarget) ? linkTarget : Path.Combine(resolved, linkTarget);
            current = Path.GetFullPath(Path.Combine(new[] { target }.Concat(segments[(i + 1)..]).ToArray()));
            goto restart;
        }

        return resolved.Length == 0 ? current : Path.GetFullPath(resolved);
    }

    /// <summary>Opens a file inside the jail for reading and verifies what was actually opened.</summary>
    public static FileStream OpenRead(string root, string relativePath)
    {
        var realRoot = GetRealPath(root);
        var real = Resolve(realRoot, relativePath);
        var stream = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        return VerifyOrThrow(stream, realRoot);
    }

    /// <summary>
    /// Opens (creating if needed) a file inside the jail for writing. The file is truncated only after
    /// the opened descriptor was verified to be inside the workspace.
    /// </summary>
    public static FileStream OpenWrite(string root, string relativePath, bool createDirectories = true)
    {
        var realRoot = GetRealPath(root);
        var real = Resolve(realRoot, relativePath);
        if (createDirectories)
        {
            var dir = Path.GetDirectoryName(real);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                // The directory chain may have been swapped while it was created: check again.
                real = Resolve(realRoot, relativePath);
            }
        }

        var stream = new FileStream(real, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        VerifyOrThrow(stream, realRoot);
        stream.SetLength(0);
        return stream;
    }

    public static async Task<byte[]> ReadAllBytesAsync(string root, string relativePath, CancellationToken ct = default)
    {
        await using var stream = OpenRead(root, relativePath);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public static async Task<string> ReadAllTextAsync(string root, string relativePath, CancellationToken ct = default)
    {
        await using var stream = OpenRead(root, relativePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    public static async Task WriteAllBytesAsync(string root, string relativePath, byte[] content, CancellationToken ct = default)
    {
        await using var stream = OpenWrite(root, relativePath);
        await stream.WriteAsync(content, ct);
    }

    public static async Task WriteAllTextAsync(string root, string relativePath, string content, CancellationToken ct = default)
    {
        // Same bytes File.WriteAllText produces: UTF-8 without a BOM.
        await WriteAllBytesAsync(root, relativePath, new UTF8Encoding(false).GetBytes(content ?? string.Empty), ct);
    }

    private static FileStream VerifyOrThrow(FileStream stream, string realRoot)
    {
        var opened = OpenedPath(stream);
        if (opened is not null && !IsInside(realRoot, opened))
        {
            stream.Dispose();
            throw new UnauthorizedAccessException("The file resolves (via a symbolic link) outside the workspace.");
        }

        return stream;
    }

    /// <summary>
    /// The path the kernel actually opened, read back from /proc/self/fd (Linux). Null where that is
    /// not available (the Windows dev box), in which case the pre-open canonical check stands alone.
    /// </summary>
    private static string? OpenedPath(FileStream stream)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var fd = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
            var target = new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
            if (target is null)
                return null;

            const string deletedSuffix = " (deleted)";
            return target.EndsWith(deletedSuffix, StringComparison.Ordinal) ? target[..^deletedSuffix.Length] : target;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

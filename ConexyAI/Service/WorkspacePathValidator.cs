using System.IO;

namespace ConexyAI.Service;

public class WorkspacePathValidator : IWorkspacePathValidator
{
    private readonly IConexyWorkspaceService _workspaceService;

    public WorkspacePathValidator(IConexyWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    public string ResolveSafePath(Guid chatId, string relativePath)
    {
        var root = Path.GetFullPath(_workspaceService.GetTaskWorkspacePath(chatId));
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, combined);

        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Path traversal outside the workspace is not allowed.");
        }

        return combined;
    }
}

/// <summary>Shared file-safety helpers reused by the editor and the file explorer API.</summary>
public static class WorkspaceFileSafety
{
    public const int MaxViewBytes = 500 * 1024; // 500 KB
    public const int MaxPreviewChars = 50 * 1024; // 50 KB preview cap for large files

    /// <summary>Detects binary content by scanning the first 8 KB for a NUL byte.</summary>
    public static bool IsBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0) return true;
        }
        return false;
    }
}

namespace ConexyAI.Service;

/// <summary>
/// Resolves a session-relative path to an absolute path inside the session workspace,
/// rejecting any path that escapes the workspace root (path traversal). This is the
/// single choke point shared by <see cref="ConexyEditorService"/> and the file explorer
/// REST API so the two never drift apart on the safety rules.
/// </summary>
public interface IWorkspacePathValidator
{
    /// <summary>
    /// Returns the absolute path for <paramref name="relativePath"/> inside the workspace
    /// of <paramref name="sessionId"/>. Throws <see cref="UnauthorizedAccessException"/>
    /// when the resolved path leaves the workspace root.
    /// </summary>
    string ResolveSafePath(Guid sessionId, string relativePath);
}

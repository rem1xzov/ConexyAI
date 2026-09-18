namespace ConexyAI.Contract;

/// <summary>A single node in the workspace file tree.</summary>
public record WorkspaceFileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    IReadOnlyList<WorkspaceFileEntry> Children);

/// <summary>Payload returned by the workspace files endpoint: flat list + nested tree.</summary>
public record WorkspaceListing(
    IReadOnlyList<string> Files,
    IReadOnlyList<WorkspaceFileEntry> Tree);

/// <summary>Payload returned by the workspace file content endpoint.</summary>
public record WorkspaceFileContent(
    string Path,
    string Name,
    string Content);

/// <summary>Payload for saving manual edits to a workspace file.</summary>
public record SaveFileDto(
    string Path,
    string? Content);

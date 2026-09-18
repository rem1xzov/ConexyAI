namespace ConexyAI.Configuration;

/// <summary>
/// Binds the <c>Workspace</c> section of appsettings.json. The workspace root is the
/// single location where agent sessions (and any artifacts the agent compiles) are
/// stored. It must live outside the source tree so the agent's <c>bin/</c>, <c>obj/</c>
/// and <c>node_modules</c> never leak into the main service's Roslyn build.
/// </summary>
public class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    /// <summary>
    /// Absolute path to the workspace root. When empty, a safe per-user path outside
    /// the repository is used (<c>%LOCALAPPDATA%/ConexyAI/workspaces</c>).
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
}

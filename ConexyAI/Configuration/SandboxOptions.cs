namespace ConexyAI.Configuration;

// SANDBOX: добавлено 2026-09-17
/// <summary>Binds the <c>Sandbox</c> section of appsettings.json for the isolated <c>bash</c> sandbox.</summary>
public class SandboxOptions
{
    public const string SectionName = "Sandbox";

    /// <summary>
    /// Docker image used for the sandbox. Must contain <c>dotnet</c>, <c>npm</c> and <c>node</c>.
    /// Built from <c>docker/sandbox/Dockerfile</c> and tagged <c>conexy-sandbox:latest</c>.
    /// </summary>
    public string Image { get; set; } = "conexy-sandbox:latest";

    /// <summary>Hard execution timeout (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Container memory limit (Docker <c>--memory</c>).</summary>
    /// <remarks>Per-tier (Free/Pro/ProMax) limits are a future enhancement; keep a shared value for now.</remarks>
    public string MemoryLimit { get; set; } = "512m";

    /// <summary>Container CPU limit (Docker <c>--cpus</c>).</summary>
    public string CpuLimit { get; set; } = "1.0";

    /// <summary>Container PID limit (Docker <c>--pids-limit</c>).</summary>
    /// <remarks>Per-tier (Free/Pro/ProMax) limits are a future enhancement; keep a shared value for now.</remarks>
    public int PidsLimit { get; set; } = 64;

    /// <summary>Size of the writable <c>/tmp</c> tmpfs (Docker <c>--tmpfs /tmp:rw,size=...</c>).</summary>
    public string TmpfsSize { get; set; } = "64m";

    /// <summary>
    /// Maximum size (in bytes) of a single file the agent can create inside the container
    /// (Docker <c>--ulimit fsize=...</c>, i.e. <c>RLIMIT_FSIZE</c>). <c>0</c> disables the limit.
    /// Defaults to <c>0</c> (unlimited) because the .NET runtime aborts with SIGXFSZ under any
    /// finite <c>RLIMIT_FSIZE</c>, which would break <c>dotnet</c> build/run tasks. To bound disk
    /// usage on the host, prefer a filesystem quota on the workspace directory (see DEPLOY.md);
    /// <c>/tmp</c> is already capped by the <c>--tmpfs size=...</c> limit.
    /// </summary>
    public ulong FileSizeLimitBytes { get; set; } = 0;
}

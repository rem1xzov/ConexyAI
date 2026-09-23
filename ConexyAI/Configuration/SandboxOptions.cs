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

    // RUN_CRASH: добавлено 2026-09-23
    /// <summary>
    /// Allows the interactive shell behind the IDE terminal and the "Run" button. That shell is NOT
    /// sandboxed: it runs as the backend itself, in the backend's container, with the backend's
    /// environment (API keys, DB credentials, JWT signing key) and its access to the Docker socket
    /// proxy — i.e. anything typed there can take over the host. It is therefore meant only for local
    /// development (appsettings.Development.json). In production "Run" returns an explicit error
    /// instead, until projects run inside the sandbox.
    /// </summary>
    public bool AllowBackendShell { get; set; }

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

    // SANDBOX_SESSIONS: добавлено 2026-09-23

    /// <summary>
    /// Minutes of sandbox inactivity after which a session's sandbox state is deleted
    /// (see <see cref="Service.SandboxIdleCleanupService"/>). <c>0</c> disables the sweeper.
    /// </summary>
    public int IdleTimeoutMinutes { get; set; } = 10;

    /// <summary>How often the idle sweeper runs (seconds).</summary>
    public int IdleSweepIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Host directory that holds one state directory per session. Mounted into every sandbox
    /// container at <c>/state</c> so package-manager caches and <c>HOME</c> survive across
    /// commands, then deleted once the session goes idle.
    /// <para>
    /// Must be given as a HOST path (same-path bind mount, like the workspace root): the backend
    /// only talks to the host Docker daemon, which resolves the path on the host.
    /// </para>
    /// </summary>
    public string StateRootPath { get; set; } = "/srv/conexyai/sandbox-state";

    /// <summary>
    /// Docker network mode for sandbox containers.
    /// <para>
    /// <c>none</c> keeps the sandbox fully offline. To let the agent install dependencies the value
    /// must be a DEDICATED network that holds nothing else — the default here is
    /// <c>conexy-sandbox-net</c>, created once on the host with
    /// <c>docker network create conexy-sandbox-net</c>. That gives the container outbound internet
    /// access (package registries) with no route to any application network: Docker isolates
    /// distinct bridge networks from each other, and the sandbox's DNS only resolves names on its
    /// own network, so <c>backend</c>/<c>postgres</c>/<c>docker-socket-proxy</c> are unreachable and
    /// unresolvable from inside it.
    /// </para>
    /// <para>
    /// Do not point this at a compose-managed application network, and do not use <c>host</c>.
    /// </para>
    /// </summary>
    public string Network { get; set; } = "conexy-sandbox-net";

    /// <summary>
    /// Value for Docker <c>--user</c> (<c>uid:gid</c>). Empty (default) resolves to the current
    /// process's effective uid, which is what makes the bind-mounted workspace writable: the
    /// workspace files are owned by the backend's user, so the sandbox must run as that same user.
    /// A mismatched uid is what produced <c>EACCES</c> on <c>node_modules</c> and package caches.
    /// </summary>
    public string User { get; set; } = string.Empty;
}

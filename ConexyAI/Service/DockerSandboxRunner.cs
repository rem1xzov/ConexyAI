using System.Diagnostics;
using System.Text;
using ConexyAI.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// SANDBOX: добавлено 2026-09-17
public record SandboxExecutionResult(bool Success, int ExitCode, string Output, bool WasTimedOut, string? ErrorType);

public interface IDockerSandboxRunner
{
    Task<bool> IsDockerAvailableAsync(CancellationToken ct = default);

    /// <param name="sessionStateHostPath">
    /// Host directory holding this session's sandbox state (HOME, package caches, installed tooling).
    /// Mounted at <c>/state</c> and reused by every command of the session, so a dependency installed
    /// by one command is still there for the next one. <c>null</c> keeps the previous behaviour of a
    /// completely stateless container.
    /// </param>
    Task<SandboxExecutionResult> RunAsync(
        string command,
        string workspaceHostPath,
        TimeSpan timeout,
        CancellationToken ct = default,
        string? sessionStateHostPath = null);
}

/// <summary>
/// Runs the <c>bash</c> tool command inside an isolated Docker container with no network,
/// a read-only root filesystem (only <c>/tmp</c> tmpfs and the bind-mounted <c>/workspace</c>
/// are writable), all Linux capabilities dropped, <c>no-new-privileges</c>, a file-size
/// ulimit, memory/CPU/PID limits, and a non-root user. No backend environment variables are
/// passed into the container, so secrets (API keys, connection strings, tokens) never leak
/// into it. If Docker is unavailable, execution is refused.
/// </summary>
public class DockerSandboxRunner : IDockerSandboxRunner
{
    private readonly IOptions<SandboxOptions> _options;
    private readonly ILogger<DockerSandboxRunner> _logger;

    public DockerSandboxRunner(IOptions<SandboxOptions> options, ILogger<DockerSandboxRunner> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<bool> IsDockerAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("version");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("{{.Server.Version}}");

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SandboxExecutionResult> RunAsync(
        string command,
        string workspaceHostPath,
        TimeSpan timeout,
        CancellationToken ct = default,
        string? sessionStateHostPath = null)
    {
        if (!await IsDockerAvailableAsync(ct))
        {
            return new SandboxExecutionResult(
                false,
                -1,
                "Sandbox unavailable: Docker daemon is required to execute terminal commands safely.",
                false,
                "sandbox_unavailable");
        }

        var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(_options.Value.TimeoutSeconds);
        var containerName = $"conexy-sandbox-{Guid.NewGuid():N}";

        // 1) Create + start detached. We deliberately avoid the foreground `docker run`
        //    (attach) flow: the docker-socket-proxy (HAProxy) does not reliably forward the
        //    hijacked attach stream back to the CLI, so stdout/stderr would be lost. Instead
        //    we read output with `docker logs` after the container exits.
        var runResult = await RunDockerCommandAsync(
            BuildDockerRunStartInfo(command, workspaceHostPath, containerName, sessionStateHostPath),
            TimeSpan.FromSeconds(30),
            ct);

        if (runResult.ExitCode != 0)
        {
            _logger.LogWarning("Sandbox container {Container} failed to start: {Output}", containerName, runResult.Output);
            await TryRemoveContainerAsync(containerName);
            return new SandboxExecutionResult(
                false,
                -1,
                $"Failed to start sandbox: {runResult.Output.Trim()}",
                false,
                "spawn_failed");
        }

        try
        {
            // 2) Wait for the container to finish, bounded by the effective timeout.
            var waitResult = await RunDockerCommandAsync(
                BuildDockerWaitStartInfo(containerName),
                effectiveTimeout,
                ct);

            if (waitResult.TimedOut)
            {
                await KillContainerAsync(containerName, CancellationToken.None);
                // Reap the killed container so `docker logs` / `docker rm` see the final state.
                await RunDockerCommandAsync(BuildDockerWaitStartInfo(containerName), TimeSpan.FromSeconds(30), CancellationToken.None);
                var timedOutOutput = await ReadLogsAsync(containerName, CancellationToken.None);
                return new SandboxExecutionResult(
                    false,
                    -1,
                    $"Command execution timed out after {(int)effectiveTimeout.TotalSeconds} seconds.\n{timedOutOutput}",
                    true,
                    "timeout");
            }

            // 3) Read the full stdout/stderr now that the container has exited.
            var output = await ReadLogsAsync(containerName, ct);

            var exitCode = ParseExitCode(waitResult.Output);
            return new SandboxExecutionResult(exitCode == 0, exitCode, output, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The whole task was cancelled: stop the container before propagating.
            await KillContainerAsync(containerName, CancellationToken.None);
            throw;
        }
        finally
        {
            await TryRemoveContainerAsync(containerName);
        }
    }

    private ProcessStartInfo BuildDockerRunStartInfo(
        string command,
        string workspaceHostPath,
        string containerName,
        string? sessionStateHostPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // No inherited environment: no `--env` values are passed, and Docker does not copy the
        // client's environment into the container, so the backend's secrets (API keys, connection
        // strings, tokens) can never leak in. The `-e` flags below are explicit non-secret path
        // overrides for the sandbox's own writable directories.
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-d"); // detached: we read output via `docker logs` (proxy-safe)
        // The sandbox image must already exist on the Docker host (built/pushed at deploy
        // time). Never let the host daemon pull images on the sandbox's behalf.
        psi.ArgumentList.Add("--pull");
        psi.ArgumentList.Add("never");
        psi.ArgumentList.Add("--name");
        psi.ArgumentList.Add(containerName);
        // AGENT_SANDBOX_NETWORK: конфигурируемый сетевой режим. По умолчанию `none` — песочница
        // полностью офлайн, и это осознанный безопасный дефолт. Ставить зависимости агенту
        // (npm/pip/dotnet add package) можно только включив `Sandbox:Network` (например `bridge`),
        // и это уже решение по безопасности, а не удобство: вместе с сетью у контейнера появляется
        // и исходящий доступ наружу.
        psi.ArgumentList.Add("--network");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(_options.Value.Network) ? "none" : _options.Value.Network.Trim());
        // Read-only root filesystem: the container may only write to /tmp (tmpfs) and the mounted
        // /workspace and /state. Any other write fails with EROFS.
        psi.ArgumentList.Add("--read-only");
        psi.ArgumentList.Add("--tmpfs");
        psi.ArgumentList.Add($"/tmp:rw,size={_options.Value.TmpfsSize}");
        // Drop every Linux capability and forbid gaining new privileges via setuid/setgid,
        // file capabilities or tools like sudo/su.
        psi.ArgumentList.Add("--cap-drop");
        psi.ArgumentList.Add("ALL");
        psi.ArgumentList.Add("--security-opt");
        psi.ArgumentList.Add("no-new-privileges");
        // Bound the size of any single file the agent can create (e.g. `dd`, huge logs).
        // Only applied when configured: the .NET runtime aborts with SIGXFSZ under any finite
        // RLIMIT_FSIZE, so it is disabled by default (see SandboxOptions).
        if (_options.Value.FileSizeLimitBytes > 0)
        {
            psi.ArgumentList.Add("--ulimit");
            psi.ArgumentList.Add($"fsize={_options.Value.FileSizeLimitBytes}");
        }
        psi.ArgumentList.Add("--memory");
        psi.ArgumentList.Add(_options.Value.MemoryLimit);
        psi.ArgumentList.Add("--cpus");
        psi.ArgumentList.Add(_options.Value.CpuLimit);
        psi.ArgumentList.Add("--pids-limit");
        psi.ArgumentList.Add(_options.Value.PidsLimit.ToString());
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add(ResolveSandboxUser());

        // AGENT_SANDBOX_SESSION_STATE: постоянное состояние сессии — HOME, кэши пакетных менеджеров
        // и всё, что агент поставил вне /workspace. Монтируется в /state, живёт между командами и
        // удаляется sweeper'ом после 10 минут простоя.
        if (!string.IsNullOrWhiteSpace(sessionStateHostPath))
        {
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add($"{sessionStateHostPath}:/state:rw");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("HOME=/state/home");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("DOTNET_CLI_HOME=/state/home");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("NPM_CONFIG_CACHE=/state/.npm");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("XDG_CACHE_HOME=/state/.cache");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("XDG_DATA_HOME=/state/.local");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("PIP_CACHE_DIR=/state/.cache/pip");
        }

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add($"{workspaceHostPath}:/workspace:rw");
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("/workspace");
        psi.ArgumentList.Add(_options.Value.Image);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        return psi;
    }

    /// <summary>
    /// Docker <c>--user</c> value for the sandbox container.
    /// <para>
    /// AGENT_SANDBOX_SESSION_STATE: defaults to the backend's own effective uid instead of a
    /// hard-coded <c>1000:1000</c>. The workspace directory is bind-mounted from the host and is
    /// owned by the backend user, so a sandbox running as a different uid can read the files but not
    /// write them — which is exactly the <c>EACCES</c> on <c>node_modules</c> and package caches the
    /// agent used to hit. Running as the same uid makes the mount writable without making anything
    /// world-writable on the host.
    /// </para>
    /// </summary>
    private string ResolveSandboxUser()
    {
        var configured = _options.Value.User?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        var uid = TryGetEffectiveUid();
        return uid is null ? "1000:1000" : $"{uid}:{uid}";
    }

    /// <summary>Effective uid of this process, or null on platforms without libc (Windows dev).</summary>
    private static uint? TryGetEffectiveUid()
    {
        if (OperatingSystem.IsWindows()) return null;

        try
        {
            return geteuid();
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint geteuid();

    private static ProcessStartInfo BuildDockerWaitStartInfo(string containerName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("wait");
        psi.ArgumentList.Add(containerName);
        return psi;
    }

    private static ProcessStartInfo BuildDockerLogsStartInfo(string containerName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("logs");
        psi.ArgumentList.Add(containerName);
        return psi;
    }

    private async Task<string> ReadLogsAsync(string containerName, CancellationToken ct)
    {
        var result = await RunDockerCommandAsync(BuildDockerLogsStartInfo(containerName), TimeSpan.FromSeconds(30), ct);
        return result.Output.TrimEnd('\n');
    }

    private static async Task KillContainerAsync(string containerName, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("kill");
            psi.ArgumentList.Add(containerName);

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            // Best-effort: the container may have already exited.
        }
    }

    private static async Task TryRemoveContainerAsync(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("rm");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(containerName);

            using var process = new Process { StartInfo = psi };
            process.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            // Best-effort cleanup; the container may already be gone.
        }
    }

    /// <summary>Runs a docker CLI subprocess, capturing combined stdout/stderr and exit code.</summary>
    private static async Task<(int ExitCode, string Output, bool TimedOut)> RunDockerCommandAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, ex.Message, false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainProcessAsync(process, stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
            await DrainProcessAsync(process, stdoutTask, stderrTask);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, CombineOutput(stdout, stderr), timedOut);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static async Task DrainProcessAsync(Process process, Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token);
        }
        catch
        {
            // Ignore: the docker client may lag the killed container.
        }

        try { await stdoutTask; } catch { /* drained */ }
        try { await stderrTask; } catch { /* drained */ }
    }

    private static int ParseExitCode(string waitOutput)
    {
        if (int.TryParse(waitOutput.Trim(), out var code))
            return code;

        return -1;
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return stdout;
        if (string.IsNullOrEmpty(stdout)) return stderr;
        return stdout.TrimEnd('\n') + "\n" + stderr;
    }
}

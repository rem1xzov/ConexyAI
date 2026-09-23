using System.Diagnostics;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

public interface IConexyRunService
{
    /// <summary>
    /// Runs the chat's project. When <paramref name="manualCommand"/> is provided it is
    /// persisted as the chat's run config; otherwise the stored config is used, falling back
    /// to auto-detection. The command runs inside the session's interactive pty so the user
    /// can stop it with Ctrl+C.
    /// </summary>
    Task<RunProjectResult> RunAsync(Guid chatId, string? manualCommand = null, CancellationToken ct = default);
}

/// <summary>
/// Resolves and executes the "Run" command (Ctrl+F5) for a chat workspace. The command is
/// detected once (or entered manually) and persisted per <c>chatId</c>, then typed into the
/// same interactive pty as the terminal panel — never a separate one-shot process — so the
/// user keeps full interactive control over a long-running server/console app.
/// </summary>
public class ConexyRunService : IConexyRunService
{
    private readonly IConexyWorkspaceService _workspaceService;
    private readonly IIdeTerminalService _terminalService;
    private readonly IHubContext<ConexyHub> _hubContext;
    private readonly ILogger<ConexyRunService> _logger;
    private readonly bool _allowBackendShell;

    // RUN_CRASH: добавлено 2026-09-23
    // Why "Run" is refused in production. It used to type the command into a shell INSIDE the backend
    // container: that shell crashed the whole backend (managed fork, see UnixPtyConnection) — the 502
    // on "Run" — and even when it started, the backend image has neither Node.js nor the .NET SDK, and
    // the UI has had no terminal to show its output since the Terminal tab was removed.
    internal const string RunUnavailableMessage =
        "Запуск проекта из рабочей области пока недоступен: раньше он выполнялся не в песочнице, а на " +
        "самом сервере, где нет Node.js и .NET SDK. Чтобы проверить проект, попросите агента собрать " +
        "или запустить его — команды агента выполняются в песочнице.";

    public ConexyRunService(
        IConexyWorkspaceService workspaceService,
        IIdeTerminalService terminalService,
        IHubContext<ConexyHub> hubContext,
        IOptions<SandboxOptions> sandboxOptions,
        ILogger<ConexyRunService> logger)
    {
        _workspaceService = workspaceService;
        _terminalService = terminalService;
        _hubContext = hubContext;
        _allowBackendShell = sandboxOptions.Value.AllowBackendShell;
        _logger = logger;
    }

    public async Task<RunProjectResult> RunAsync(Guid chatId, string? manualCommand = null, CancellationToken ct = default)
    {
        // Checked before command detection: otherwise the UI would first ask for a run command and
        // only then refuse to run it.
        if (!_allowBackendShell)
        {
            _logger.LogInformation("Run refused for chat {ChatId}: Sandbox:AllowBackendShell is off.", chatId);
            return new RunProjectResult { NeedsManualConfig = false, Error = RunUnavailableMessage };
        }

        string? command = manualCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = await _workspaceService.GetRunCommandAsync(chatId, ct);
            if (string.IsNullOrWhiteSpace(command))
            {
                command = DetectRunCommand(chatId);
                if (string.IsNullOrWhiteSpace(command))
                {
                    return new RunProjectResult { NeedsManualConfig = true };
                }

                await _workspaceService.SetRunCommandAsync(chatId, command, ct);
            }
        }
        else
        {
            command = command.Trim();
            await _workspaceService.SetRunCommandAsync(chatId, command, ct);
        }

        // TEMPORARY Windows workaround: the hand-rolled ConPTY binding does not attach the
        // shell (powershell.exe/cmd.exe spawn a visible window and no output reaches the
        // pty). On Windows dev, run in a plain visible console window like VS/Rider do. The
        // Linux/Unix pty path below stays untouched for production.
        if (OperatingSystem.IsWindows())
        {
            return RunInVisibleConsole(chatId, command);
        }

        // Ensure the pty exists even if the terminal tab was not opened yet.
        await _terminalService.StartAsync(chatId, ct);

        // Stop any process still occupying the shell before re-running. On a fresh prompt
        // this only clears the current (empty) line, which is harmless.
        await _terminalService.SendInputAsync(chatId, "\x03", ct);

        // Label the command in the shared terminal feed, then type it into the pty.
        await SendUserCommandAsync(chatId, command, ct);
        await _terminalService.SendInputAsync(chatId, command + "\r", ct);

        _logger.LogInformation("Run command for chat {ChatId}: {Command}", chatId, command);
        return new RunProjectResult { Command = command, NeedsManualConfig = false };
    }

    /// <summary>Heuristically builds a run command for the workspace root.</summary>
    private string? DetectRunCommand(Guid chatId)
    {
        var root = _workspaceService.GetTaskWorkspacePathIfExists(chatId);
        if (root == null) return null;

        // 1) A .csproj in the root or a top-level subfolder.
        var csproj = FindTopLevelProjectFile(root);
        if (csproj != null)
            return $"dotnet run --project {Quote(csproj)}";

        // 2) A package.json with a dev/start script.
        var packageJson = Path.Combine(root, "package.json");
        if (File.Exists(packageJson))
        {
            var npm = TryResolveNpmScript(packageJson);
            if (npm != null) return npm;
        }

        return null;
    }

    private string? FindTopLevelProjectFile(string root)
    {
        // Root itself, then the immediate subdirectories.
        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate subdirectories of {Root} while detecting run command", root);
            subdirectories = Enumerable.Empty<string>();
        }

        var candidates = new List<string> { root };
        candidates.AddRange(subdirectories);

        foreach (var dir in candidates)
        {
            try
            {
                var project = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (project != null)
                    return Path.GetRelativePath(root, project).Replace('\\', '/');
            }
            catch (IOException) { /* unreadable subdir; skip */ }
            catch (UnauthorizedAccessException) { /* unreadable subdir; skip */ }
        }

        return null;
    }

    private static string? TryResolveNpmScript(string packageJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Prefer "dev", fall back to "start".
            if (scripts.TryGetProperty("dev", out var dev) && dev.ValueKind == JsonValueKind.String)
                return "npm run dev";
            if (scripts.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.String)
                return "npm run start";

            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task SendUserCommandAsync(Guid chatId, string command, CancellationToken ct)
    {
        var data = "\x1b[1;32m[you]\x1b[0m $ " + NormalizeCrlf(command) + "\r\n";
        await _hubContext.Clients.Group($"task_{chatId}").SendAsync("TerminalOutput", new TerminalOutputEvent
        {
            SessionId = chatId,
            Data = data,
            Source = "user"
        }, ct);
    }

    /// <summary>
    /// Temporary Windows workaround: launch the run command in a visible <c>cmd.exe</c> console
    /// window (like VS/Rider do). We deliberately do NOT redirect stdout — with
    /// <c>UseShellExecute=true</c> the window is visible and its output is not mirrored into the
    /// UI terminal (the ConPTY binding that would enable that is broken on Windows).
    /// </summary>
    private RunProjectResult RunInVisibleConsole(Guid chatId, string command)
    {
        var workspacePath = _workspaceService.GetTaskWorkspacePath(chatId);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"{command}\"", // /k keeps the window open after the command finishes
            WorkingDirectory = workspacePath,
            UseShellExecute = true, // true => visible window, no stream redirection
            CreateNoWindow = false,
        };

        try
        {
            Process.Start(psi);
            _logger.LogInformation("Launched run command in visible console for chat {ChatId}: {Command}", chatId, command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch visible console for chat {ChatId}", chatId);
            throw;
        }

        return new RunProjectResult { Command = command, NeedsManualConfig = false, LaunchedInSeparateWindow = true };
    }

    private static string NormalizeCrlf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

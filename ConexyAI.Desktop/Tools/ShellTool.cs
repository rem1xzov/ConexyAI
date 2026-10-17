using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

/// <summary>Спрашивает пользователя, можно ли выполнить команду на его ПК.</summary>
public interface ICommandApprover
{
    Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct);

    /// <summary>Подтверждение с заголовком («Чтение вне проекта», «Установка» …) для инструментов ПК.</summary>
    Task<bool> ApproveAsync(string title, string details, string workingDirectory, CancellationToken ct) =>
        ApproveAsync(details, workingDirectory, ct);

    /// <summary>Действие от имени пользователя во внешнем сервисе (отправка письма): спрашиваем всегда, даже в авто-режиме.</summary>
    Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        ApproveAsync(title, details, where, ct);
}

/// <summary>Правила для команд: что запрещено безусловно и что безопасно выполнять без вопроса.</summary>
public static partial class CommandPolicy
{
    // Катастрофические команды запрещены даже с «разрешать всё»: подтверждение тут не поможет,
    // слишком легко нажать «y» не глядя.
    private static readonly Regex[] Forbidden =
    {
        new(@"\brm\s+(-[a-z]*\s+)*-[a-z]*[rf][a-z]*\s+(/|~|\$HOME|\*)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bformat\s+[a-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(del|erase|rd|rmdir)\b[^\n]*\s[a-z]:\\\s*($|\s|\*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"remove-item\b[^\n]*-recurse[^\n]*\s[a-z]:\\?\s*($|\s|\*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(diskpart|mkfs(\.\w+)?|fdisk|bcdedit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdd\s+[^\n]*of=/dev/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\breg\s+delete\s+hk", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(shutdown|restart-computer|stop-computer)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(curl|wget|iwr|invoke-webrequest)[^\n|]*\|\s*(sh|bash|iex|invoke-expression|powershell|pwsh)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(\.ssh[\\/]|\.aws[\\/]|\.gnupg[\\/]|\.netrc|id_rsa|id_ed25519)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly HashSet<string> ReadOnlyHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "pwd", "cat", "type", "head", "tail", "wc", "tree", "get-childitem", "get-content", "where", "which",
    };

    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "branch", "rev-parse", "ls-files",
    };

    public static string? ForbiddenReason(string command) =>
        Forbidden.Any(r => r.IsMatch(command))
            ? "Команда запрещена политикой безопасности клиента (разрушительная операция, доступ к ключам или загрузка-и-запуск кода)."
            : null;

    /// <summary>Только чтение, без цепочек и перенаправлений — можно без вопроса.</summary>
    public static bool IsReadOnly(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0 || ChainChars().IsMatch(trimmed))
            return false;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;
        if (parts[0].Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2 || !ReadOnlyGit.Contains(parts[1]))
                return false;
            // `git diff/log --output=file` пишет файл.
            if (parts.Any(p => p.StartsWith("--output", StringComparison.OrdinalIgnoreCase)))
                return false;
            // `git branch x` создаёт ветку, `git branch -D x` удаляет — без вопроса только просмотр.
            return !parts[1].Equals("branch", StringComparison.OrdinalIgnoreCase)
                   || parts.Skip(2).All(p => ReadOnlyBranchFlags.Contains(p));
        }
        return ReadOnlyHeads.Contains(parts[0]);
    }

    private static readonly HashSet<string> ReadOnlyBranchFlags = new(StringComparer.Ordinal)
    {
        "-a", "--all", "-r", "--remotes", "-v", "-vv", "--verbose", "-l", "--list", "--show-current",
    };

    [GeneratedRegex(@"[;&|<>`]|\$\(|\r|\n")]
    private static partial Regex ChainChars();
}

/// <summary>bash / terminal_exec: команда в рабочей папке, с подтверждением, таймаутом и убийством дерева процессов.</summary>
public sealed partial class ShellTool
{
    // С запасом на первую сборку со скачиванием зависимостей (ForgeGradle, Maven, Android, npm). Сервер ждёт дольше.
    private const int DefaultTimeoutSeconds = 600;
    internal const int MaxTimeoutSeconds = 3600;
    private const int MaxOutputChars = 40_000;

    private readonly PathGuard _guard;
    private readonly ICommandApprover _approver;
    private readonly Func<CancellationToken, Task<string?>>? _gitHubToken;

    public ShellTool(PathGuard guard, ICommandApprover approver, Func<CancellationToken, Task<string?>>? gitHubToken = null)
    {
        _guard = guard;
        _approver = approver;
        _gitHubToken = gitHubToken;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(^|[\s;&|(])git(\.exe)?\s", RegexOptions.IgnoreCase)]
    private static partial Regex GitCall();

    public async Task<ToolResult> RunAsync(ToolArgs args, string tool, CancellationToken ct)
    {
        var command = args.Required("command", tool);
        var timeout = Math.Clamp(args.Int("timeout_seconds") ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
        // Оболочка новая на каждый вызов, `cd` не сохраняется — подпапка задаётся явно и не выходит за проект.
        var cwd = args.Str("cwd") is { Length: > 0 } dir ? _guard.Resolve(dir) : _guard.Root;
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Папка '{args.Str("cwd")}' не найдена в проекте.");
        var result = await ExecuteAsync(command, timeout, requireApproval: true, ct, useCmd: tool == "cmd_exec", workingDirectory: cwd);
        // Если сама оболочка не запустилась — подсказываем модели обходной путь.
        if (result.IsError && result.Output.StartsWith("Не удалось запустить команду") && tool != "cmd_exec")
            return ToolResult.Fail(result.Output + "\nОбход: выполни ту же команду через cmd_exec, а для файлов используй file_read/grep/glob/apply_patch.");
        return result;
    }

    /// <summary>Команда, уже подтверждённая вызывающим инструментом (установка программ).</summary>
    public Task<ToolResult> RunApprovedAsync(string command, int timeoutSeconds, CancellationToken ct) =>
        ExecuteAsync(command, timeoutSeconds, requireApproval: false, ct, skipPolicy: false);

    /// <summary>
    /// Оболочка для команды. PATH берётся из реестра заново, чтобы только что установленные node/python/docker
    /// были видны без перезапуска клиента.
    /// </summary>
    public static ProcessStartInfo CreateShell(string command, bool useCmd, bool wrapExitCode = true)
    {
        var psi = BuildStartInfo(command, useCmd, wrapExitCode);
        if (OperatingSystem.IsWindows())
        {
            // Переменные окружения из реестра заново: установщики прописывают JAVA_HOME, GRADLE_HOME, ANDROID_HOME и т.п.,
            // а запущенный до установки клиент их иначе не увидит. Сначала системные, потом пользовательские.
            foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
            {
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables(target))
                {
                    var name = (string)entry.Key;
                    if (!name.Equals("PATH", StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
                        psi.Environment[name] = Environment.ExpandEnvironmentVariables(value);
                }
            }
            var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
            var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
            var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var merged = string.Join(';', $"{machine};{user};{current}".Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            psi.Environment["PATH"] = merged;
        }
        return psi;
    }

    /// <summary>Команда, собранная самим клиентом (тесты/диагностика) — без вопроса.</summary>
    public Task<ToolResult> RunTrustedAsync(string command, int timeoutSeconds, CancellationToken ct) =>
        ExecuteAsync(command, timeoutSeconds, requireApproval: false, ct);

    private async Task<ToolResult> ExecuteAsync(
        string command, int timeoutSeconds, bool requireApproval, CancellationToken ct, bool useCmd = false, bool skipPolicy = false,
        string? workingDirectory = null)
    {
        workingDirectory ??= _guard.Root;
        if (!skipPolicy && CommandPolicy.ForbiddenReason(command) is { } reason)
            return ToolResult.Fail(reason);

        if (requireApproval && !CommandPolicy.IsReadOnly(command)
            && !await _approver.ApproveAsync(useCmd ? "cmd" : "Bash", command, workingDirectory, ct))
            return ToolResult.Fail("Пользователь отклонил выполнение команды. Не повторяй её; предложи другой путь или спроси, что делать.");

        var psi = CreateShell(command, useCmd);
        psi.WorkingDirectory = workingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        // Токен GitHub с сайта подаётся git'у через окружение процесса (не попадает ни в текст команды,
        // ни в .git/config). Только для github.com и только если в команде есть вызов git.
        string? ghToken = null;
        if (_gitHubToken is not null && GitCall().IsMatch(command))
        {
            try { ghToken = await _gitHubToken(ct); }
            catch (Exception ex) when (ex is HttpRequestException or AuthRejectedException) { }
        }

        if (!string.IsNullOrEmpty(ghToken))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + ghToken));
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + basic;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        var output = new StringBuilder();
        var sync = new object();
        void Append(string? line)
        {
            if (line is null) return;
            lock (sync) output.AppendLine(line);
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("Не удалось запустить команду: " + ex.Message);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try
        {
            // Ждём выход самого процесса, а не EOF вывода: запущенные командой фоновые потомки
            // (Start-Process, dev-серверы) держат pipe открытым, и ожидание EOF висело бы до таймаута.
            await exited.Task.WaitAsync(timeoutCts.Token);
            process.WaitForExit(TimeSpan.FromSeconds(3)); // дочитать хвост вывода
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut)
                throw;
        }

        string text;
        lock (sync) text = output.ToString().TrimEnd();
        if (!string.IsNullOrEmpty(ghToken))
            text = text.Replace(ghToken, "***");
        text = Truncate(text);

        if (timedOut)
            return ToolResult.Fail($"{text}\n[Команда прервана по таймауту {timeoutSeconds} с]".TrimStart());

        var code = process.ExitCode;
        return new ToolResult($"{text}\nExit code: {code}".TrimStart(), code != 0);
    }

    private static ProcessStartInfo BuildStartInfo(string command, bool useCmd, bool wrapExitCode)
    {
        if (OperatingSystem.IsWindows() && useCmd)
        {
            var cmd = new ProcessStartInfo("cmd.exe");
            cmd.ArgumentList.Add("/d");
            cmd.ArgumentList.Add("/s");
            cmd.ArgumentList.Add("/c");
            cmd.ArgumentList.Add("chcp 65001 >nul & " + command);
            return cmd;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!wrapExitCode)
            {
                var bg = new ProcessStartInfo(PowerShellExe);
                foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command })
                    bg.ArgumentList.Add(a);
                return bg;
            }
            var psi = new ProcessStartInfo(PowerShellExe);
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            // UTF-8 вывод и честный код возврата: и нативных команд ($LASTEXITCODE), и упавших
            // cmdlet'ов ($?). Хвост — с новой строки, чтобы `# комментарий` в команде его не съел.
            psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; $global:LASTEXITCODE = 0\n" + command
                                 + "\n$__ok = $?; if ($LASTEXITCODE) { exit $LASTEXITCODE } elseif (-not $__ok) { exit 1 }");
            return psi;
        }

        var sh = new ProcessStartInfo("/bin/sh");
        sh.ArgumentList.Add("-c");
        sh.ArgumentList.Add(command);
        return sh;
    }

    private static readonly Lazy<string?> Pwsh = new(FindPwsh);

    /// <summary>PowerShell 7 (если установлен): в нём работают `&&`/`||`, которые модели пишут по привычке.</summary>
    public static bool HasPwsh => OperatingSystem.IsWindows() && Pwsh.Value is not null;

    private static string PowerShellExe => Pwsh.Value ?? "powershell.exe";

    private static string? FindPwsh()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[] { Path.Combine(program, "PowerShell", "7", "pwsh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir.Trim(), "pwsh.exe")));
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { /* мусор в PATH */ }
        }
        return null;
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxOutputChars)
            return text;
        var half = MaxOutputChars / 2;
        return text[..half] + $"\n[... вывод обрезан: {text.Length - MaxOutputChars} символов ...]\n" + text[^half..];
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop.Tools;

/// <summary>Спрашивает пользователя, можно ли выполнить команду на его ПК.</summary>
public interface ICommandApprover
{
    Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct);
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
            return parts.Length > 1 && ReadOnlyGit.Contains(parts[1]);
        return ReadOnlyHeads.Contains(parts[0]);
    }

    [GeneratedRegex(@"[;&|<>`]|\$\(|\r|\n")]
    private static partial Regex ChainChars();
}

/// <summary>bash / terminal_exec: команда в рабочей папке, с подтверждением, таймаутом и убийством дерева процессов.</summary>
public sealed partial class ShellTool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 900;
    private const int MaxOutputChars = 30_000;

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
        return await ExecuteAsync(command, timeout, requireApproval: true, ct);
    }

    /// <summary>Команда, собранная самим клиентом (тесты/диагностика) — без вопроса.</summary>
    public Task<ToolResult> RunTrustedAsync(string command, int timeoutSeconds, CancellationToken ct) =>
        ExecuteAsync(command, timeoutSeconds, requireApproval: false, ct);

    private async Task<ToolResult> ExecuteAsync(string command, int timeoutSeconds, bool requireApproval, CancellationToken ct)
    {
        if (CommandPolicy.ForbiddenReason(command) is { } reason)
            return ToolResult.Fail(reason);

        if (requireApproval && !CommandPolicy.IsReadOnly(command)
            && !await _approver.ApproveAsync(command, _guard.Root, ct))
            return ToolResult.Fail("Пользователь отклонил выполнение команды. Не повторяй её; предложи другой путь или спроси, что делать.");

        var psi = BuildStartInfo(command);
        psi.WorkingDirectory = _guard.Root;
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

        using var process = new Process { StartInfo = psi };
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
            await process.WaitForExitAsync(timeoutCts.Token);
            process.WaitForExit(); // дочитать потоки вывода
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

    private static ProcessStartInfo BuildStartInfo(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("powershell.exe");
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            // Сохраняем код возврата нативных команд и UTF-8 вывод.
            psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command + "; if ($LASTEXITCODE) { exit $LASTEXITCODE }");
            return psi;
        }

        var sh = new ProcessStartInfo("/bin/sh");
        sh.ArgumentList.Add("-c");
        sh.ArgumentList.Add(command);
        return sh;
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxOutputChars)
            return text;
        var half = MaxOutputChars / 2;
        return text[..half] + $"\n[... вывод обрезан: {text.Length - MaxOutputChars} символов ...]\n" + text[^half..];
    }
}

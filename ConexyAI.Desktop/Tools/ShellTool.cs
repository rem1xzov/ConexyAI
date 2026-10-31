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

    /// <summary>
    /// BASH_DESCRIPTION: подтверждение команды вместе с человеческим описанием — пользователь видит,
    /// ЧТО делает команда, а не только её флаги. По умолчанию описание игнорируется.
    /// </summary>
    Task<bool> ApproveCommandAsync(string command, string? description, string workingDirectory, CancellationToken ct) =>
        ApproveAsync("Bash", command, workingDirectory, ct);

    /// <summary>Действие от имени пользователя во внешнем сервисе (отправка письма): спрашиваем всегда, даже в авто-режиме.</summary>
    Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        ApproveAsync(title, details, where, ct);

    /// <summary>Вопрос агента пользователю (ask_user). null — ответить некому.</summary>
    Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct) => Task.FromResult<string?>(null);
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
    };

    // SECRET_PATHS: места с ключами и учётными данными — не читаются ни командой, ни файловыми инструментами,
    // ни с подтверждением. Сюда же собственная папка настроек клиента (config.json с ключом, mcp.json, google.json,
    // mail.json, services.json): иначе внедрённая инструкция уводит токены пользователя «обычным cat».
    // Обход через вычисление пути ([Environment]::GetFolderPath…) этим не ловится, но такая команда
    // не простая и без подтверждения не выполняется.
    private static readonly Regex[] SecretPaths =
    {
        new(@"(\.ssh|\.aws|\.gnupg|\.netrc|_netrc|\.git-credentials|\.pgpass)(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.docker[\\/]config\.json|\.kube[\\/]config", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // .docker/.kube/.azure в домашней папке (а не одноимённые папки проекта с compose-файлами).
        new(@"(~|\bhome\}?|users[\\/][^\\/\s""']+|userprofile%?|/root)[\\/]\.(docker|kube|azure)(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"id_rsa|id_ed25519|id_ecdsa|id_dsa", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.config[\\/](gh|conexyai|google-chrome|chromium|microsoft-edge|bravesoftware)(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"appdata(%|[\\/]roaming)?[\\/]conexyai(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(google[\\/]chrome|microsoft[\\/]edge|bravesoftware[\\/]brave-browser|yandex[\\/]yandexbrowser|chromium)[\\/]user data", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(mozilla[\\/]firefox|\.mozilla)(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"application support[\\/](google[\\/]chrome|microsoft edge|firefox|conexyai)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>Папка настроек клиента (ключ, токены коннекторов) — абсолютный путь для проверки разрешённых путей.</summary>
    public static string ClientConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI");

    /// <summary>Почему путь/команда касается секретов (null — не касается).</summary>
    public static string? SecretPathReason(string text)
    {
        if (SecretPaths.Any(r => r.IsMatch(text)))
            return "Доступ к этому месту запрещён политикой клиента (ключи, учётные данные, профили браузеров, настройки ConexyAI).";
        var dir = ClientConfigDir;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return dir.Length > 3 && text.Contains(dir, cmp)
            ? "Доступ к настройкам клиента ConexyAI запрещён политикой (там ключи и токены)."
            : null;
    }

    private static readonly HashSet<string> ReadOnlyHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "pwd", "cat", "type", "head", "tail", "wc", "tree", "get-childitem", "get-content", "where", "which",
    };

    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "branch", "rev-parse", "ls-files",
    };

    public static string? ForbiddenReason(string command) =>
        Forbidden.Any(r => r.IsMatch(command)) || SecretPathReason(command) is not null
            ? "Команда запрещена политикой безопасности клиента (разрушительная операция, доступ к ключам или загрузка-и-запуск кода)."
            : null;

    /// <summary>
    /// Только чтение, без цепочек и перенаправлений — можно без вопроса. С <paramref name="root"/> дополнительно
    /// требуется, чтобы каждый аргумент-путь (относительно <paramref name="cwd"/>) оставался внутри проекта.
    /// </summary>
    public static bool IsReadOnly(string command, string? root = null, string? cwd = null)
    {
        var trimmed = command.Trim();
        // STRICT_READONLY: на Windows команда идёт в PowerShell, где `cat (iex (irm …))`, `$(…)`, `@(…)`,
        // `[IO.File]::…`, `{…}` и `%` исполняют код. Без вопроса — только «простые» команды из букв, цифр
        // и безопасной пунктуации; всё остальное спрашиваем.
        if (trimmed.Length == 0 || !IsSimple(trimmed))
            return false;

        var parts = Tokens(trimmed);
        if (parts.Count == 0)
            return false;
        if (parts[0].Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count < 2 || !ReadOnlyGit.Contains(parts[1]))
                return false;
            // `git diff/log --output=file` пишет файл; `git diff --no-index a b` читает любые файлы вне репозитория.
            if (parts.Any(p => p.StartsWith("--output", StringComparison.OrdinalIgnoreCase)
                               || p.StartsWith("--no-index", StringComparison.OrdinalIgnoreCase)
                               || p.StartsWith("--ext-diff", StringComparison.OrdinalIgnoreCase)
                               || p.StartsWith("--textconv", StringComparison.OrdinalIgnoreCase)))
                return false;
            // `git branch x` создаёт ветку, `git branch -D x` удаляет — без вопроса только просмотр.
            return !parts[1].Equals("branch", StringComparison.OrdinalIgnoreCase)
                   || parts.Skip(2).All(p => ReadOnlyBranchFlags.Contains(p));
        }
        if (!ReadOnlyHeads.Contains(parts[0]))
            return false;
        // READONLY_INSIDE_ROOT: «cat C:\Users\me\AppData\…» — тоже чтение, но не проекта. Без вопроса —
        // только пути внутри рабочей папки.
        return root is null || parts.Skip(1).All(p => ArgumentInside(p, root, cwd ?? root));
    }

    /// <summary>
    /// Простая команда: буквы, цифры, пробел и « - _ . / \ : * = + ~ ^ » и кавычки. Нет ни цепочек, ни
    /// перенаправлений, ни подстановок, ни вызовов .NET/скриптблоков PowerShell, ни переменных окружения.
    /// </summary>
    public static bool IsSimple(string command) => SimpleCommand().IsMatch(command.Trim());

    // Запятая исключена: в PowerShell `cat a,\x` — массив путей, второй из которых проверку не проходил бы.
    [GeneratedRegex(@"^[\p{L}\p{N} \-_./\\:*=+~^'""]+$")]
    private static partial Regex SimpleCommand();

    private static List<string> Tokens(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\'')).Where(t => t.Length > 0).ToList();

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsInside(string full, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(root, PathCmp) || full.StartsWith(root + Path.DirectorySeparatorChar, PathCmp);
    }

    /// <summary>Аргумент команды остаётся внутри проекта (флаги проверяются по значению после «=»/«:»).</summary>
    private static bool ArgumentInside(string token, string root, string cwd)
    {
        var value = token;
        if (value.StartsWith('-'))
        {
            var sep = value.IndexOfAny(['=', ':']);
            if (sep < 0)
                return true;
            value = value[(sep + 1)..];
            if (value.Length == 0)
                return true;
        }
        // `~` в bash/PowerShell — домашняя папка; `env:`, `HKLM:`, `cert:` — провайдеры PowerShell, не файлы.
        if (value.StartsWith('~'))
            return false;
        var colon = value.IndexOf(':');
        if (colon >= 0 && !(colon == 1 && OperatingSystem.IsWindows() && Path.IsPathFullyQualified(value)))
            return false;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(cwd, value));
            if (!IsInside(full, root))
                return false;
            // Ссылка/junction внутри проекта, ведущая наружу, — тоже «вне проекта».
            new PathGuard(root).Resolve(full.Replace("*", "_"));
            return SecretPathReason(full) is null;
        }
        catch (Exception ex) when (ex is ToolException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Есть ли в команде пути за пределами проекта (абсолютные наружу, «..», «~», переменные окружения). Грубая
    /// эвристика для удалённых подтверждений демона: песочницей она не является.
    /// </summary>
    public static bool ReferencesOutside(string command, string root)
    {
        if (Regex.IsMatch(command, @"\$env:|\$HOME\b|\$\{?HOME|%[A-Za-z_]+%|\[(System\.)?(IO\.|Environment)", RegexOptions.IgnoreCase))
            return true;
        foreach (var raw in Regex.Split(command, @"[\s;&|<>()`""'=,{}]+"))
        {
            var token = raw.Trim();
            if (token.Length == 0 || token.Contains("://"))
                continue;
            if (token.StartsWith('~'))
                return true;
            // Флаги cmd вида /s, /b, /?.
            if (OperatingSystem.IsWindows() && Regex.IsMatch(token, @"^/[A-Za-z?]{1,3}$"))
                continue;
            var looksLikePath = Path.IsPathRooted(token) || token.Contains("..");
            if (!looksLikePath)
                continue;
            try
            {
                if (!IsInside(Path.GetFullPath(Path.IsPathRooted(token) ? token : Path.Combine(root, token)), root))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> ReadOnlyBranchFlags = new(StringComparer.Ordinal)
    {
        "-a", "--all", "-r", "--remotes", "-v", "-vv", "--verbose", "-l", "--list", "--show-current",
    };

    /// <summary>
    /// Есть ли в команде цепочки, перенаправления, подстановки или иные метасимволы оболочки (тогда по префиксу
    /// её не разрешаем). STRICT_READONLY: та же строгость, что и для команд чтения.
    /// </summary>
    public static bool IsCompound(string command) => !IsSimple(command);
}

/// <summary>bash / terminal_exec: команда в рабочей папке, с подтверждением, таймаутом и убийством дерева процессов.</summary>
public sealed partial class ShellTool
{
    // С запасом на первую сборку со скачиванием зависимостей (ForgeGradle, Maven, Android, npm). Сервер ждёт дольше.
    private const int DefaultTimeoutSeconds = 600;
    internal const int MaxTimeoutSeconds = 3600;
    // TOKEN_ECONOMY: вывод команд тоже едет в контекст на каждом шаге. Логи сборки читаются
    // по краям (начало + конец), 16k символов — ~4k токенов.
    private const int MaxOutputChars = 16_000;

    private readonly PathGuard _guard;
    private readonly ICommandApprover _approver;
    private readonly Func<CancellationToken, Task<string?>>? _gitHubToken;

    public ShellTool(PathGuard guard, ICommandApprover approver, Func<CancellationToken, Task<string?>>? gitHubToken = null)
    {
        _guard = guard;
        _approver = approver;
        _gitHubToken = gitHubToken;
    }

    // GIT_TOKEN_SCOPE: токен получает только команда, которая САМА начинается с git/gh. Раньше хватало «git»
    // где-то в строке: `printenv; git status` отдавал модели GIT_CONFIG_VALUE_0 с токеном в base64.
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(git|gh)(\.exe)?\s", RegexOptions.IgnoreCase)]
    private static partial Regex GitCall();

    public async Task<ToolResult> RunAsync(ToolArgs args, string tool, CancellationToken ct)
    {
        var command = args.Required("command", tool);
        // BASH_DESCRIPTION: модель обязана объяснить намерение; старые вызовы без описания не ломаем.
        var description = args.Str("description")?.Trim();
        var timeout = Math.Clamp(args.Int("timeout_seconds") ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
        // Оболочка новая на каждый вызов, `cd` не сохраняется — подпапка задаётся явно и не выходит за проект.
        var cwd = args.Str("cwd") is { Length: > 0 } dir ? _guard.Resolve(dir) : _guard.Root;
        if (!Directory.Exists(cwd))
            return ToolResult.Fail($"Папка '{args.Str("cwd")}' не найдена в проекте.");
        var result = await ExecuteAsync(command, timeout, requireApproval: true, ct, useCmd: tool == "cmd_exec",
            workingDirectory: cwd, description: description);
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

    /// <summary>Подтверждение команды через общий approver (консоль или сайт в режиме демона).</summary>
    public Task<bool> ApproveAsync(string title, string details, CancellationToken ct) =>
        _approver.ApproveAsync(title, details, _guard.Root, ct);

    private async Task<ToolResult> ExecuteAsync(
        string command, int timeoutSeconds, bool requireApproval, CancellationToken ct, bool useCmd = false, bool skipPolicy = false,
        string? workingDirectory = null, string? description = null)
    {
        workingDirectory ??= _guard.Root;
        if (!skipPolicy && CommandPolicy.ForbiddenReason(command) is { } reason)
            return ToolResult.Fail(reason);

        if (requireApproval && !CommandPolicy.IsReadOnly(command, _guard.Root, workingDirectory)
            && !await _approver.ApproveCommandAsync(command, description, workingDirectory, ct))
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
        string? ghBasic = null;
        if (_gitHubToken is not null && GitCall().IsMatch(command))
        {
            try { ghToken = await _gitHubToken(ct); }
            catch (Exception ex) when (ex is HttpRequestException or AuthRejectedException) { }
        }

        if (!string.IsNullOrEmpty(ghToken))
        {
            var basic = ghBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + ghToken));
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
        if (!string.IsNullOrEmpty(ghBasic))
            text = text.Replace(ghBasic, "***");
        // BASH_COMPRESS: ANSI и баннеры не нужны модели никогда, а от сборки — только итог и окно ошибки.
        text = Compress(command, text);

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

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial System.Text.RegularExpressions.Regex AnsiEscape();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(dotnet\s+(build|test|publish|restore)|msbuild|gradlew?|mvn|npm\s+(run\s+)?(build|test)|yarn\s+(build|test)|pnpm\s+(build|test)|cargo\s+(build|test|check)|go\s+(build|test)|pytest|jest|vitest|tsc|make)\b")]
    private static partial System.Text.RegularExpressions.Regex BuildCommand();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\berror\b|\bfailed\b|\bFAIL(ED)?\b|Traceback|Unhandled exception|fatal error|Cannot find module|✗",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ErrorLine();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^\s*(\d+\s+(Error|Warning)\(s\)|Build (succeeded|FAILED).*|Passed!.*|Failed!.*|Tests? (run|passed|failed).*|Test Run (Failed|Successful).*|Time Elapsed:.*)\s*$")]
    private static partial System.Text.RegularExpressions.Regex SummaryLine();

    /// <summary>
    /// Вывод покомпактнее: снять ANSI, выбросить простыни пустых строк, а от сборки/тестов оставить
    /// итог и по 2 строки контекста вокруг ошибок. Остальное — по краям.
    /// </summary>
    private static string Compress(string command, string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;
        var text = AnsiEscape().Replace(raw, string.Empty).Replace("\r\n", "\n").TrimEnd();
        text = System.Text.RegularExpressions.Regex.Replace(text, "\n{3,}", "\n\n");

        if (!BuildCommand().IsMatch(command.ToLowerInvariant()) || !ErrorLine().IsMatch(text))
            return Truncate(text);

        var lines = text.Split('\n');
        var picked = new SortedSet<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ErrorLine().IsMatch(lines[i]))
                continue;
            for (var j = Math.Max(0, i - 2); j <= Math.Min(lines.Length - 1, i + 2); j++)
                picked.Add(j);
        }
        if (picked.Count == 0)
            return Truncate(text);

        var body = string.Join('\n', picked.Select(i => lines[i]));
        if (body.Length > 6_000)
            body = body[..6_000] + "\n[... ошибки обрезаны ...]";
        var summary = string.Join('\n', lines.Where(l => SummaryLine().IsMatch(l)).TakeLast(8));
        return $"[вывод сжат: {lines.Length} строк → итог и окно вокруг ошибок]\n"
               + (summary.Length > 0 ? summary + "\n\n" : string.Empty)
               + body;
    }
}

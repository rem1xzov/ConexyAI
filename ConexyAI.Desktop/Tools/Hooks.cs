using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConexyAI.Desktop;

namespace ConexyAI.Desktop.Tools;

/// <summary>Решение хука до выполнения инструмента.</summary>
public sealed record HookDecision(bool Denied, string? Reason, string? Context = null);

/// <summary>
/// HOOKS: хуки проекта из <c>.conexy/hooks.json</c> — команды, которые выполняются до и после вызова
/// инструмента. Формат совместим по смыслу с Claude Code: событие → группы по matcher → обработчики.
/// Хуки — это чужой код из репозитория, поэтому они выполняются только после явного доверия папке
/// (<see cref="HookTrust"/>); в демоне и headless-режиме недоверенные хуки пропускаются.
/// </summary>
public sealed class HookRunner
{
    private const int MaxOutput = 20_000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root;
    private readonly Action<string> _log;
    private readonly List<HookGroup> _pre;
    private readonly List<HookGroup> _post;
    private readonly List<HookGroup> _postFailure;

    private HookRunner(string root, Action<string> log, List<HookGroup> pre, List<HookGroup> post, List<HookGroup> postFailure)
    {
        _root = root;
        _log = log;
        _pre = pre;
        _post = post;
        _postFailure = postFailure;
    }

    /// <summary>Хуки включены: файл есть и папка доверена.</summary>
    public bool Enabled => _pre.Count > 0 || _post.Count > 0 || _postFailure.Count > 0;

    public static string FilePathFor(string root) => Path.Combine(root, ".conexy", "hooks.json");

    /// <summary>
    /// Загружает хуки проекта. Nедоверенный файл не выполняется (вернётся null); причина пишется в лог.
    /// <paramref name="trusted"/> нужен для selftest и для явного доверия из CLI.
    /// </summary>
    public static HookRunner? Load(string root, Action<string> log, bool? trusted = null)
    {
        // CI/headless: --no-hooks выставляет этот флаг — хуки из репозитория не выполняются.
        if (Environment.GetEnvironmentVariable("CONEXY_DISABLE_HOOKS") == "1")
            return null;
        var path = FilePathFor(root);
        if (!File.Exists(path))
            return null;
        HookSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<HookSettings>(File.ReadAllText(path), Json) ?? new HookSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log("Хуки: файл " + path + " не прочитан (" + ex.Message + ") — хуки пропущены.");
            return null;
        }

        var isTrusted = trusted ?? HookTrust.IsTrusted(root, HookTrust.Hash(path));
        if (!isTrusted)
        {
            log("Хуки: в проекте есть .conexy/hooks.json, но папка не доверена — хуки не выполняются. Доверить: conexy hooks trust");
            return null;
        }

        var pre = settings.Hooks?.PreToolUse ?? settings.PreToolUse ?? [];
        var post = settings.Hooks?.PostToolUse ?? settings.PostToolUse ?? [];
        var postFailure = settings.Hooks?.PostToolUseFailure ?? settings.PostToolUseFailure ?? [];
        return new HookRunner(root, log, pre, post, postFailure);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Проверка до выполнения инструмента. null — можно выполнять.</summary>
    public async Task<HookDecision?> BeforeAsync(string tool, string argumentsJson, CancellationToken ct)
    {
        var (denied, reason, context) = await RunGroupsAsync(_pre, "PreToolUse", tool, argumentsJson, ct);
        if (denied)
            return new HookDecision(true, reason ?? "Вызов заблокирован хуком проекта.", context);
        return context is { Length: > 0 } ? new HookDecision(false, null, context) : null;
    }

    /// <summary>Проверка после выполнения инструмента. Возвращает текст, который нужно дописать к результату.</summary>
    public async Task<string?> AfterAsync(string tool, string argumentsJson, bool isError, CancellationToken ct)
    {
        var (denied, reason, context) = await RunGroupsAsync(isError ? _postFailure : _post,
            isError ? "PostToolUseFailure" : "PostToolUse", tool, argumentsJson, ct);
        var notes = new List<string>();
        if (context is { Length: > 0 })
            notes.Add(context);
        if (denied && reason is { Length: > 0 })
            notes.Add("[hook] " + reason);
        return notes.Count == 0 ? null : "\n\n" + string.Join("\n", notes);
    }

    private async Task<(bool Denied, string? Reason, string? Context)> RunGroupsAsync(
        List<HookGroup> groups, string eventName, string tool, string argumentsJson, CancellationToken ct)
    {
        var contexts = new List<string>();
        // Хуки выполняются последовательно: порядок в файле — это порядок проверок.
        foreach (var group in groups)
        {
            if (!Matches(group.Matcher, tool))
                continue;
            foreach (var handler in group.Hooks ?? [])
            {
                if (handler.Type is { Length: > 0 } type && !type.Equals("command", StringComparison.OrdinalIgnoreCase))
                    continue; // http/prompt/agent-хуки в десктопе не поддерживаются — молча пропускаем
                var (deny, reason, context) = await RunCommandAsync(handler, eventName, tool, argumentsJson, ct);
                if (context is { Length: > 0 })
                    contexts.Add(context);
                if (deny)
                    return (true, reason, contexts.Count == 0 ? null : string.Join("\n", contexts));
            }
        }
        return (false, null, contexts.Count == 0 ? null : string.Join("\n", contexts));
    }

    /// <summary>Matcher: «*», список через | или запятую — точное имя инструмента; иначе регулярное выражение.</summary>
    internal static bool Matches(string? matcher, string tool)
    {
        if (string.IsNullOrWhiteSpace(matcher) || matcher.Trim() is "*")
            return true;
        var value = matcher.Trim();
        if (value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '|' or ',' or ' '))
        {
            return value.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(name => name.Equals(tool, StringComparison.OrdinalIgnoreCase));
        }
        try
        {
            return Regex.IsMatch(tool, value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<(bool Denied, string? Reason, string? Context)> RunCommandAsync(
        HookHandler handler, string eventName, string tool, string argumentsJson, CancellationToken ct)
    {
        var command = handler.Command;
        if (string.IsNullOrWhiteSpace(command))
            return (false, null, null);

        var timeout = handler.Timeout is > 0 ? TimeSpan.FromSeconds(Math.Clamp(handler.Timeout.Value, 1, 600)) : DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        ProcessStartInfo psi;
        try
        {
            psi = BuildStartInfo(handler, command, eventName, tool);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _log($"Хуки: {eventName} «{tool}» не запущен ({ex.Message}).");
            return (false, null, null);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return (false, null, null);
            await process.StandardInput.WriteAsync(Payload(eventName, tool, argumentsJson));
            process.StandardInput.Close();

            var stdout = ReadCapped(process.StandardOutput);
            var stderr = ReadCapped(process.StandardError);
            await process.WaitForExitAsync(cts.Token);
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();

            var (deny, reason, context) = Interpret(process.ExitCode, output, error);
            if (deny)
                _log($"Хуки: {eventName} «{tool}» заблокирован ({reason}).");
            return (deny, reason, context);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log($"Хуки: {eventName} «{tool}» не уложился в {timeout.TotalSeconds:0} с — пропущен.");
            return (false, null, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Сломанный хук не должен ломать задачу: как и в Claude Code без onFailure=block.
            _log($"Хуки: {eventName} «{tool}» завершился ошибкой ({ex.Message}).");
            return (false, null, null);
        }
    }

    private ProcessStartInfo BuildStartInfo(HookHandler handler, string command, string eventName, string tool)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.Environment["CONEXY_PROJECT_DIR"] = _root;
        psi.Environment["CONEXY_ROOT"] = _root;
        psi.Environment["CONEXY_TOOL"] = tool;
        psi.Environment["CONEXY_HOOK_EVENT"] = eventName;

        var expanded = Expand(command);
        if (handler.Args is { Count: > 0 } args)
        {
            // Exec-форма: без оболочки, аргументы как есть.
            psi.FileName = expanded;
            foreach (var arg in args)
                psi.ArgumentList.Add(Expand(arg));
            return psi;
        }

        var shell = handler.Shell;
        if (OperatingSystem.IsWindows())
        {
            if (shell?.Equals("powershell", StringComparison.OrdinalIgnoreCase) == true
                || expanded.TrimEnd('"').EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = FindPowerShell();
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                if (expanded.TrimEnd('"').EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    psi.ArgumentList.Add("-File");
                    psi.ArgumentList.Add(expanded);
                }
                else
                {
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add(expanded);
                }
                return psi;
            }
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(expanded);
            return psi;
        }

        psi.FileName = shell?.Equals("powershell", StringComparison.OrdinalIgnoreCase) == true ? "pwsh" : "/bin/sh";
        psi.ArgumentList.Add(shell?.Equals("powershell", StringComparison.OrdinalIgnoreCase) == true ? "-Command" : "-c");
        psi.ArgumentList.Add(expanded);
        return psi;
    }

    private static string FindPowerShell()
    {
        var pwsh = ShellTool.FindPwsh();
        return pwsh ?? "powershell.exe";
    }

    private string Expand(string value) => value
        .Replace("${CONEXY_PROJECT_DIR}", _root, StringComparison.OrdinalIgnoreCase)
        .Replace("${CONEXY_ROOT}", _root, StringComparison.OrdinalIgnoreCase)
        .Replace("$CONEXY_PROJECT_DIR", _root, StringComparison.OrdinalIgnoreCase)
        .Replace("$CONEXY_ROOT", _root, StringComparison.OrdinalIgnoreCase);

    private string Payload(string eventName, string tool, string argumentsJson)
    {
        JsonElement input;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            input = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            input = JsonDocument.Parse("{}").RootElement.Clone();
        }
        return JsonSerializer.Serialize(new
        {
            hook_event_name = eventName,
            tool_name = tool,
            tool_input = input,
            cwd = _root,
            project_dir = _root,
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Разбор ответа: exit 2 или JSON с deny блокируют вызов; additionalContext/systemMessage идут модели.
    /// Текст в stderr для exit 2 — причина отказа.
    /// </summary>
    private static (bool Denied, string? Reason, string? Context) Interpret(int exitCode, string stdout, string stderr)
    {
        if (stdout.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                string? context = null;
                if (root.TryGetProperty("hookSpecificOutput", out var specific) && specific.ValueKind == JsonValueKind.Object)
                {
                    if (specific.TryGetProperty("permissionDecision", out var decision)
                        && decision.GetString() is { } decisionText
                        && decisionText.Equals("deny", StringComparison.OrdinalIgnoreCase))
                    {
                        var reason = specific.TryGetProperty("permissionDecisionReason", out var r) ? r.GetString() : null;
                        return (true, reason ?? "Вызов заблокирован хуком проекта.", null);
                    }
                    if (specific.TryGetProperty("additionalContext", out var c))
                        context = c.GetString();
                }
                if (root.TryGetProperty("decision", out var topDecision)
                    && topDecision.GetString() is { } topText
                    && topText.Equals("block", StringComparison.OrdinalIgnoreCase))
                {
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    return (true, reason ?? "Вызов заблокирован хуком проекта.", null);
                }
                if (context is null && root.TryGetProperty("systemMessage", out var message))
                    context = message.GetString();
                if (context is { Length: > 0 })
                    return (false, null, context);
            }
            catch (JsonException)
            {
                // Не JSON — обрабатываем как обычный текст ниже.
            }
        }

        if (exitCode == 2)
            return (true, string.IsNullOrWhiteSpace(stderr) ? "Вызов заблокирован хуком проекта." : stderr, null);
        return (false, null, null);
    }

    private static async Task<string> ReadCapped(StreamReader reader)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (builder.Length < MaxOutput)
                builder.Append(buffer, 0, Math.Min(read, MaxOutput - builder.Length));
        }
        return builder.ToString();
    }

    private sealed class HookSettings
    {
        public HookEvents? Hooks { get; set; }
        public List<HookGroup>? PreToolUse { get; set; }
        public List<HookGroup>? PostToolUse { get; set; }
        public List<HookGroup>? PostToolUseFailure { get; set; }
    }

    private sealed class HookEvents
    {
        public List<HookGroup>? PreToolUse { get; set; }
        public List<HookGroup>? PostToolUse { get; set; }
        public List<HookGroup>? PostToolUseFailure { get; set; }
    }

    private sealed class HookGroup
    {
        public string? Matcher { get; set; }
        public List<HookHandler>? Hooks { get; set; }
    }

    private sealed class HookHandler
    {
        public string? Type { get; set; }
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public string? Shell { get; set; }
        public int? Timeout { get; set; }
    }
}

/// <summary>
/// HOOK_TRUST: хуки — это код из репозитория. Папка доверяется один раз, хеш файла запоминается:
/// если hooks.json изменился, доверие сбрасывается и хуки снова не выполняются до подтверждения.
/// </summary>
public static class HookTrust
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(ProjectStore.ConfigDir, "trusted-hooks.json");

    public static string Hash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static bool IsTrusted(string root, string hash)
    {
        if (Environment.GetEnvironmentVariable("CONEXY_TRUST_HOOKS") == "1")
            return true;
        if (hash.Length == 0)
            return false;
        try
        {
            lock (Sync)
            {
                return Load().TryGetValue(Normalize(root), out var stored)
                       && stored.Equals(hash, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void Trust(string root)
    {
        var path = HookRunner.FilePathFor(root);
        if (!File.Exists(path))
            return;
        lock (Sync)
        {
            var map = Load();
            map[Normalize(root)] = Hash(path);
            Write(map);
        }
    }

    public static void Forget(string root)
    {
        lock (Sync)
        {
            var map = Load();
            if (map.Remove(Normalize(root)))
                Write(map);
        }
    }

    public static bool HasTrustedEntry(string root)
    {
        lock (Sync)
            return Load().ContainsKey(Normalize(root));
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Write(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        ClientConfig.WriteOwnerOnly(FilePath, JsonSerializer.Serialize(map, Json));
    }

    private static string Normalize(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            .Replace('\\', '/')
            .ToLowerInvariant();
}

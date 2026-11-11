using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>
/// `conexy doctor` — одна команда для поддержки: вход, сервер, SignalR, рабочая папка, обновления,
/// хуки, прокси, демон и внешние утилиты. Ничего не меняет, только проверяет.
/// </summary>
public static class Doctor
{
    private sealed record Check(string Name, string Status, string Detail);

    public static async Task<int> RunAsync(string[] args)
    {
        var json = args.Contains("--json");
        var checks = new List<Check>();
        void Add(string name, string status, string detail) => checks.Add(new(name, status, detail));

        Add("клиент", "PASS", $"v{Ui.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        var configWritable = TryWrite(ProjectStore.ConfigDir, out var configError);
        Add("папка настроек", configWritable ? "PASS" : "FAIL",
            configWritable ? ProjectStore.ConfigDir : configError ?? ProjectStore.ConfigDir);

        var config = ClientConfig.Load();
        var root = Path.GetFullPath(Option(args, "--dir") ?? Directory.GetCurrentDirectory());
        var unsafeRoot = Workspace.WhyUnsafe(root);
        Add("рабочая папка", unsafeRoot is null && Directory.Exists(root) ? "PASS" : "FAIL",
            unsafeRoot ?? (Directory.Exists(root) ? root : "не существует: " + root));

        var proxy = ClientConfig.LoadHttpProxy();
        Add("HTTP-прокси", proxy is null ? "PASS" : "WARN", NetProxy.Describe(proxy));

        var hooksFile = HookRunner.FilePathFor(root);
        if (File.Exists(hooksFile))
        {
            var trusted = HookTrust.IsTrusted(root, HookTrust.Hash(hooksFile));
            Add("хуки проекта", trusted ? "PASS" : "WARN",
                trusted ? hooksFile : "есть, но папка не доверена — conexy hooks trust");
        }
        else
            Add("хуки проекта", "PASS", "нет .conexy/hooks.json");

        Add("PowerShell 7", ShellTool.HasPwsh ? "PASS" : "WARN", ShellTool.HasPwsh ? "pwsh найден" : "pwsh не найден (используется Windows PowerShell)");
        Add("Git", HasCommand("git") ? "PASS" : "WARN", HasCommand("git") ? "git найден" : "git не найден");
        Add("Node.js", HasCommand("node") ? "PASS" : "WARN", HasCommand("node") ? "node найден (нужен для MCP)" : "node не найден — часть MCP-серверов не запустится");
        Add("winget", !OperatingSystem.IsWindows() || HasCommand("winget") ? "PASS" : "WARN",
            OperatingSystem.IsWindows() ? (HasCommand("winget") ? "winget найден" : "winget не найден — install_software недоступен") : "не Windows");
        Add("демон", Daemon.IsRunning() ? "PASS" : "WARN", Daemon.IsRunning()
            ? "работает · журнал: " + Daemon.LogPath
            : "не запущен (это нормально, если не нужен «Мой ПК»)");

        var key = config.GetKey();
        var url = config.ServerUrl;
        if (!ClientConfig.IsAllowedServerUrl(url) || string.IsNullOrWhiteSpace(key))
        {
            Add("вход", "FAIL", "нужен https-адрес и ключ: conexy login");
        }
        else
        {
            Add("сервер", "PASS", url!);
            using var server = new ServerClient(url!, key);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await server.GetTokenAsync(cts.Token);
                Add("ключ", "PASS", "принят сервером");
            }
            catch (Exception ex) when (ex is AuthRejectedException or HttpRequestException or TaskCanceledException)
            {
                Add("ключ", "FAIL", ex.Message);
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var usage = await server.GetUsageAsync(cts.Token);
                Add("тариф", usage is null ? "WARN" : "PASS",
                    usage is null ? "сервер не вернул тариф" : $"{usage.Tier} · агент {usage.AgentUsed:N0}/{FormatLimit(usage.AgentLimit)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Add("тариф", "WARN", ex.Message);
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var update = await Updater.CheckAsync(server);
                Add("обновление", update is null ? "PASS" : "WARN",
                    update is null ? "последняя версия" : $"доступна v{update.Version}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Add("обновление", "WARN", ex.Message);
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var hub = new HubConnectionBuilder()
                    .WithUrl($"{url!.TrimEnd('/')}/hubs/conexy", options =>
                    {
                        options.AccessTokenProvider = async () => await server.GetTokenAsync();
                        if (server.Proxy is { } webProxy)
                            options.Proxy = webProxy;
                    })
                    .Build();
                await using (hub)
                {
                    await hub.StartAsync(cts.Token);
                    Add("SignalR", "PASS", "соединение установлено");
                    await hub.StopAsync(cts.Token);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or HubException or InvalidOperationException)
            {
                Add("SignalR", "FAIL", ex.Message);
            }
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(checks, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true,
            }));
        }
        else
        {
            foreach (var check in checks)
                Console.WriteLine($"{check.Status,-4}  {check.Name,-18} {check.Detail}");
            var failures = checks.Count(c => c.Status == "FAIL");
            var warnings = checks.Count(c => c.Status == "WARN");
            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? warnings == 0 ? "Всё в порядке." : $"Предупреждений: {warnings} — работе не мешают."
                : $"Провалено проверок: {failures}.");
        }

        return checks.Any(c => c.Status == "FAIL") ? 1 : 0;
    }

    private static string FormatLimit(long limit) => limit >= long.MaxValue / 2 ? "∞" : limit.ToString("N0");

    private static bool TryWrite(string dir, out string? error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".doctor-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HasCommand(string name)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("where.exe", name)
                : new ProcessStartInfo("which", name);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConexyAI.Desktop;

// WEB_DEVICE: добавлено 2026-10-18
/// <summary>
/// `conexy daemon`: фоновое подключение без консоли. Регистрирует ПК и проекты, не входит ни в какой чат и
/// выполняет задачи с сайта, пока его не остановят. Подтверждения и вопросы — на сайте.
/// </summary>
public static class Daemon
{
    private const string MutexName = "Global\\ConexyAI.Daemon";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ConexyAI";
    private const long MaxLogBytes = 5L * 1024 * 1024;

    private static readonly object LogSync = new();
    private static bool _console = true;

    public static string LogPath => Path.Combine(ProjectStore.ConfigDir, "daemon.log");

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    /// <summary>Запущен ли демон (именованный мьютекс занят другим процессом).</summary>
    public static bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return true; }
        return false;
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (LogSync)
        {
            try
            {
                Directory.CreateDirectory(ProjectStore.ConfigDir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.WriteAllText(LogPath, string.Empty);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (_console)
            {
                try { Console.WriteLine(line); }
                catch (IOException) { _console = false; }
            }
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--install"))
            return Install();
        if (args.Contains("--uninstall"))
            return Uninstall();

        if (args.Contains("--background"))
        {
            _console = false;
            if (OperatingSystem.IsWindows())
                FreeConsole();
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine("Демон ConexyAI уже запущен на этом ПК.");
            return 1;
        }

        var config = ClientConfig.Load();
        var key = config.GetKey();
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(key))
        {
            Log("Нет ключа: сначала выполните conexy login.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        };

        using var server = new ServerClient(config.ServerUrl, key);
        var device = new DeviceTools(daemon: true, console: null, Log, server.GetGitHubTokenAsync);
        Log($"Демон запущен (v{Ui.Version}), сервер {server.BaseUrl}, проектов: {device.Projects.Projects.Count}. Журнал: {LogPath}");
        if (device.Projects.Projects.Count == 0)
            Log("Проекты не настроены: conexy projects add <папка>");

        var delay = TimeSpan.FromSeconds(2);
        while (!cts.IsCancellationRequested)
        {
            await using var session = new AgentSession(server, null, null, device);
            try
            {
                await session.ConnectAsync(cts.Token);
                Log("Подключено.");
                delay = TimeSpan.FromSeconds(2);
                // Повторная регистрация раз в минуту: на сервере побеждает последняя, и после выхода из
                // интерактивного сеанса задачи с сайта снова идут сюда.
                while (!cts.IsCancellationRequested && !session.Closed.IsCompleted)
                {
                    await Task.WhenAny(session.Closed, Task.Delay(TimeSpan.FromSeconds(60), cts.Token));
                    if (cts.IsCancellationRequested || session.Closed.IsCompleted)
                        break;
                    try { await session.RegisterAsync(); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* переподключение само зарегистрирует */ }
                }
                if (!cts.IsCancellationRequested)
                    Log("Соединение закрыто, переподключаюсь…");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Не удалось подключиться: {ex.Message}. Повтор через {delay.TotalSeconds:0} с");
                try { await Task.Delay(delay, cts.Token); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }

        Log("Демон остановлен.");
        return 0;
    }

    private static string StartCommand =>
        $"\"{Environment.ProcessPath}\" daemon --background";

    private static int Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            PrintUnixInstructions();
            return 0;
        }
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(RunValue, StartCommand);
        Console.WriteLine($"Автозапуск включён: демон стартует при входе в Windows.\n  {StartCommand}");
        if (!IsRunning())
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "daemon --background") { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine("Демон запущен в фоне. Журнал: " + LogPath);
        }
        return 0;
    }

    private static int Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Отключите автозапуск: systemctl --user disable --now conexy (Linux) или launchctl unload ~/Library/LaunchAgents/ru.conexyai.daemon.plist (macOS).");
            return 0;
        }
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        Console.WriteLine("Автозапуск отключён." + (IsRunning() ? " Запущенный демон работает до выхода из системы (или завершите процесс conexy)." : string.Empty));
        return 0;
    }

    private static void PrintUnixInstructions()
    {
        var exe = Environment.ProcessPath;
        if (OperatingSystem.IsMacOS())
        {
            Console.WriteLine($"""
                Автозапуск на macOS (launchd): создайте ~/Library/LaunchAgents/ru.conexyai.daemon.plist

                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>Label</key><string>ru.conexyai.daemon</string>
                  <key>ProgramArguments</key><array><string>{exe}</string><string>daemon</string></array>
                  <key>RunAtLoad</key><true/>
                  <key>KeepAlive</key><true/>
                </dict></plist>

                и выполните: launchctl load ~/Library/LaunchAgents/ru.conexyai.daemon.plist
                """);
            return;
        }
        Console.WriteLine($"""
            Автозапуск на Linux (systemd, пользовательский сервис): создайте ~/.config/systemd/user/conexy.service

            [Unit]
            Description=ConexyAI daemon
            After=network-online.target

            [Service]
            ExecStart={exe} daemon
            Restart=always
            RestartSec=10

            [Install]
            WantedBy=default.target

            и выполните: systemctl --user daemon-reload && systemctl --user enable --now conexy
            """);
    }
}

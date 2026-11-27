using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ConexyAI.Desktop;

// VISUAL_DESKTOP: визуальный десктоп — та же веб-версия ConexyAI, но в отдельном окне-приложении на
// этом ПК, рядом с консольным клиентом. Переключаться можно в любой момент: окно и консоль работают
// с одним аккаунтом и одним ПК.
//
// Как устроено: веб-приложение открывается в Chromium (Edge/Chrome) в режиме установленного
// приложения (`--app`), со своим профилем в %APPDATA%\ConexyAI\visual-desktop — поэтому там
// сохраняется вход, история и настройки, как в обычном браузере, но это отдельное окно без вкладок
// и адресной строки. Вклад входа переносится одноразовым кодом (POST /api/auth/desktop-handoff →
// ссылка `?handoff=`), который SPA меняет на сессию: пользователю не нужно входить второй раз.
//
// Окно ищется по уникальному заголовку "ConexyAI Desktop" — его выставляет веб-приложение на маршруте
// /desktop. Так клиент поднимает/сворачивает именно это окно, а не вкладку браузера пользователя.
public static class VisualDesktop
{
    /// <summary>Заголовок окна визуального десктопа; его выставляет SPA на маршруте <see cref="Route"/>.</summary>
    public const string WindowTitle = "ConexyAI Desktop";

    /// <summary>Маршрут веб-приложения, который открывается в визуальном окне.</summary>
    public const string Route = "/desktop";

    /// <summary>Combo переключения консоль ↔ визуальный режим. Три модификатора — чтобы не отнимать
    /// у других программ частые сочетания (Ctrl+Alt+V — «вставить как» в Word, Ctrl+Shift+V — консоли/IDE).</summary>
    public const string HotkeyLabel = "Ctrl+Alt+Shift+V";

    /// <summary>Страница входа в обычном браузере (запасной вариант, когда Chromium-браузера нет).</summary>
    public const string FallbackUrl = "https://conexyai.ru";

    private const int HotkeyId = 0x0CE1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkV = 0x56;
    private const uint WmHotkey = 0x0312;
    private const uint WmClose = 0x0010;
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    private static readonly object Sync = new();
    private static readonly object LogSync = new();
    private static int _hotkeyStarted;
    private static Process? _browser;

    /// <summary>Профиль визуального окна: свой вход, история и настройки, отдельно от браузера пользователя.</summary>
    public static string ProfileDir => Path.Combine(ProjectStore.ConfigDir, "visual-desktop");

    public static string LogPath => Path.Combine(ProjectStore.ConfigDir, "visual-desktop.log");

    /// <summary>Визуальный десктоп есть только на Windows: остальным доступна веб-версия в браузере.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Удалось ли занять <see cref="HotkeyLabel"/> под переключение консоль/визуальный режим.</summary>
    public static bool HotkeyRegistered { get; private set; }

    /// <summary>Горячую клавишу вообще пытались зарегистрировать (интерактивный клиент это делает всегда).</summary>
    public static bool HotkeyStarted { get; private set; }

    /// <summary>Открыто ли сейчас окно визуального десктопа.</summary>
    public static bool IsOpen => CurrentWindow() != IntPtr.Zero;

    /// <summary>
    /// Открывает визуальный десктоп или поднимает уже открытое окно. Возвращает строку для консоли:
    /// что именно произошло (включая запасной вариант «открыл в браузере»).
    /// </summary>
    public static async Task<string> OpenAsync(ServerClient? server, CancellationToken ct = default)
    {
        var open = CurrentWindow();
        if (open != IntPtr.Zero)
        {
            FocusWindow(open);
            return "Визуальный десктоп уже открыт — поднял окно." + ToggleHint;
        }

        if (!OperatingSystem.IsWindows())
        {
            var fallback = BuildUrl(server?.BaseUrl, handoff: null);
            OpenInDefaultBrowser(fallback);
            return "Визуальный десктоп доступен только на Windows. Открыл веб-версию в браузере: " + fallback;
        }

        var browser = FindBrowser();
        if (browser is null)
        {
            // Chromium (Edge/Chrome) — обязательное условие режима приложения. Вместо ошибки открываем
            // обычный браузер: веб-версия работает так же, просто без отдельного окна и переключения.
            var fallback = BuildUrl(server?.BaseUrl, handoff: null);
            OpenInDefaultBrowser(fallback);
            return "Не нашёл Microsoft Edge или Google Chrome — открыл веб-версию в браузере. " +
                   "Поставьте Edge, чтобы получить отдельное окно и переключение " + HotkeyLabel + ".";
        }

        // VISUAL_DESKTOP: одноразовый код для входа в окне. Если сервер его не выдал, страница
        // откроется как обычно — пользователь сможет войти в ней сам, как на сайте.
        var handoff = server is null ? null : await server.CreateVisualHandoffAsync(ct);
        var url = BuildUrl(server?.BaseUrl, handoff);
        Launch(browser, url);

        // Окно создаётся асинхронно; ждём его появления, чтобы сразу поднять на передний план.
        // Пока страница грузится, Chromium показывает временный заголовок «host_/desktop», поэтому
        // CurrentWindow ищет и по нему, и по процессу запуска, а не только по финальному заголовку.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(250, ct);
            var window = CurrentWindow();
            if (window == IntPtr.Zero)
                continue;
            FocusWindow(window);
            return "Открыл визуальный десктоп: это веб-версия в отдельном окне, вход — как на сайте." + ToggleHint;
        }

        return "Браузер запущен, но окно визуального десктопа пока не появилось. " + ToggleHint;
    }

    /// <summary>Поднимает открытое окно визуального десктопа (или открывает его).</summary>
    public static async Task<string> ToggleAsync()
    {
        var window = CurrentWindow();
        if (window != IntPtr.Zero)
        {
            if (IsForeground(window))
            {
                ShowWindow(window, SwMinimize);
                FocusConsole();
                return "Визуальный десктоп свёрнут — вы в консоли.";
            }

            FocusWindow(window);
            return "Переключился на визуальный десктоп.";
        }

        return await OpenAsync(ServerClient.Current);
    }

    /// <summary>Сворачивает окно визуального десктопа и возвращает фокус в консоль.</summary>
    public static bool ShowConsole()
    {
        var window = CurrentWindow();
        if (window == IntPtr.Zero)
            return false;

        if (!IsIconic(window))
            ShowWindow(window, SwMinimize);
        FocusConsole();
        return true;
    }

    /// <summary>Сворачивает окно визуального десктопа, не трогая фокус (его уже держит консоль).</summary>
    public static bool Minimize()
    {
        var window = CurrentWindow();
        if (window == IntPtr.Zero)
            return false;
        if (!IsIconic(window))
            ShowWindow(window, SwMinimize);
        return true;
    }

    /// <summary>Закрывает окно визуального десктопа (сессия входа остаётся в его профиле).</summary>
    public static bool Close()
    {
        var window = CurrentWindow();
        if (window == IntPtr.Zero)
            return false;
        PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>
    /// Удаляет профиль визуального окна вместе с сохранённой сессией. Вызывается при выходе из
    /// аккаунта и при смене ключа, чтобы на общем ПК визуальный десктоп не остался вошедшим под
    /// прежним пользователем.
    /// </summary>
    public static void Reset()
    {
        var window = CurrentWindow();
        var pid = window != IntPtr.Zero ? ProcessIdOf(window) : 0;
        if (window != IntPtr.Zero)
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);

        // Даём Chromium закрыть окно штатно, затем удаляем профиль. Фоновый процесс Edge может ещё
        // держать файлы профиля — его (и только его: у окна отдельный профиль) добиваем после.
        for (var attempt = 0; attempt < 20 && CurrentWindow() != IntPtr.Zero; attempt++)
            Thread.Sleep(100);

        if (!DeleteProfile())
        {
            KillBrowser(pid);
            Thread.Sleep(500);
            DeleteProfile();
        }
    }

    private static bool DeleteProfile()
    {
        try
        {
            if (!Directory.Exists(ProfileDir))
                return true;
            Directory.Delete(ProfileDir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("profile cleanup failed: " + ex.GetType().Name);
            return false;
        }
    }

    private static void KillBrowser(int pid)
    {
        if (pid <= 0)
            return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Процесс уже завершился — это нормальный исход.
        }
    }

    /// <summary>Человеческое состояние визуального десктопа для команды `/visual status`.</summary>
    public static string Describe()
    {
        if (!OperatingSystem.IsWindows())
            return "Визуальный десктоп доступен только на Windows. Веб-версия открывается в браузере.";

        var browser = FindBrowser();
        var window = CurrentWindow();
        var lines = new List<string>
        {
            window != IntPtr.Zero ? "Окно визуального десктопа открыто." : "Окно визуального десктопа закрыто.",
            browser is null
                ? "Microsoft Edge / Google Chrome не найдены — доступна только веб-версия в браузере."
                : "Браузер: " + browser,
            "Профиль окна (вход и настройки): " + ProfileDir,
            HotkeyRegistered
                ? "Переключение консоль ↔ визуальный режим: " + HotkeyLabel + "."
                : HotkeyStarted
                    ? HotkeyLabel + " занят другим приложением — переключайтесь через /visual и /console."
                    : "Горячая клавиша не запущена: она работает в интерактивном клиенте (conexy).",
        };
        return string.Join("\n  ", lines);
    }

    /// <summary>Включает глобальное переключение <see cref="HotkeyLabel"/>. Безопасно вызывать несколько раз.</summary>
    public static void StartHotkey()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Interlocked.Exchange(ref _hotkeyStarted, 1) == 1)
            return;

        HotkeyStarted = true;
        var thread = new Thread(HotkeyLoop)
        {
            IsBackground = true,
            Name = "conexy-visual-hotkey",
        };
        thread.Start();
    }

    /// <summary>Ссылка на визуальный десктоп: маршрут /desktop и одноразовый код входа, если он есть.</summary>
    internal static string BuildUrl(string? serverUrl, string? handoff)
    {
        var root = string.IsNullOrWhiteSpace(serverUrl) ? FallbackUrl : serverUrl.TrimEnd('/');
        var url = root + Route;
        return string.IsNullOrEmpty(handoff) ? url : $"{url}?handoff={Uri.EscapeDataString(handoff)}";
    }

    /// <summary>Путь к Chromium-браузеру (Edge приоритетнее) или null, если его нет.</summary>
    internal static string? FindBrowser()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("LocalAppData"),
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var edge = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(edge))
                return edge;

            var chrome = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(chrome))
                return chrome;
        }

        return null;
    }

    private static string ToggleHint => HotkeyRegistered ? " " + HotkeyLabel + " — переключиться на консоль." : string.Empty;

    private static void Launch(string browser, string url)
    {
        var start = new ProcessStartInfo
        {
            FileName = browser,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Не наследуем консольные потоки клиента: иначе `conexy | tee log` (или CI-пайп) ждёт
            // закрытия браузера, потому что его процесс держит наш stdout.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        // Режим установленного приложения: без вкладок, адресной строки и меню браузера.
        start.ArgumentList.Add("--app=" + url);
        start.ArgumentList.Add("--user-data-dir=" + ProfileDir);
        start.ArgumentList.Add("--window-size=1440,900");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-session-crashed-bubble");

        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(ProfileDir);
                _browser?.Dispose();
                _browser = Process.Start(start);
                Log("launched browser pid=" + (_browser?.Id.ToString() ?? "?"));
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log("launch failed: " + ex.GetType().Name);
                OpenInDefaultBrowser(url);
            }
        }
    }

    private static void OpenInDefaultBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log("default browser failed: " + ex.GetType().Name);
        }
    }

    private static void HotkeyLoop()
    {
        // Очередь сообщений нужна до RegisterHotKey: WM_HOTKEY доставляется в очередь потока.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModAlt | ModShift, VkV))
        {
            Log("hotkey " + HotkeyLabel + " is already taken");
            return;
        }

        HotkeyRegistered = true;
        Log("hotkey " + HotkeyLabel + " registered");
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WmHotkey && message.wParam == (IntPtr)HotkeyId)
                    _ = ToggleFromHotkeyAsync();
            }
        }
        finally
        {
            HotkeyRegistered = false;
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }
    }

    /// <summary>
    /// Переключение по горячей клавише. Печатать в консоль нельзя: TUI в этот момент может читать
    /// ввод, поэтому режимы просто переключаются без сообщений.
    /// </summary>
    private static async Task ToggleFromHotkeyAsync()
    {
        try
        {
            await ToggleAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log("hotkey toggle failed: " + ex.GetType().Name);
        }
    }

    private static IntPtr WindowHandle()
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        var exact = FindWindow(null, WindowTitle);
        if (exact != IntPtr.Zero)
            return exact;

        // Запас на случай, если Chromium добавит к заголовку суффикс: ищем по началу строки.
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            var title = TitleOf(handle);
            if (title.StartsWith(WindowTitle, StringComparison.OrdinalIgnoreCase))
            {
                found = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Окно визуального десктопа для команд и переключения. Кроме финального заголовка учитываются
    /// процесс, который мы сами запустили, и временный заголовок Chromium «host_/desktop», пока
    /// страница грузится: иначе в первые секунды окно выглядело бы закрытым и его открыли бы второй раз.
    /// </summary>
    private static IntPtr CurrentWindow()
    {
        var byTitle = WindowHandle();
        if (byTitle != IntPtr.Zero)
            return byTitle;

        var launched = LaunchedWindowHandle();
        if (launched != IntPtr.Zero)
            return launched;

        var provisional = ProvisionalTitle(ServerClient.Current?.BaseUrl);
        return provisional is null ? IntPtr.Zero : FindWindow(null, provisional);
    }

    /// <summary>Главное окно браузера, запущенного этим процессом (Chromium создаёт его до загрузки страницы).</summary>
    private static IntPtr LaunchedWindowHandle()
    {
        lock (Sync)
        {
            if (_browser is null)
                return IntPtr.Zero;
            try
            {
                _browser.Refresh();
                return _browser.HasExited ? IntPtr.Zero : _browser.MainWindowHandle;
            }
            catch (InvalidOperationException)
            {
                // Процесс уже завершился и освободил handle.
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Временный заголовок окна-приложения у Chromium, пока у документа нет <c>&lt;title&gt;</c>:
    /// «host_/desktop» (порт в заголовок не попадает). Нужен только для поиска окна, не для показа.
    /// </summary>
    internal static string? ProvisionalTitle(string? serverUrl)
    {
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(serverUrl) ? FallbackUrl : serverUrl, UriKind.Absolute, out var uri))
            return null;
        return uri.Host + "_" + Route;
    }

    /// <summary>
    /// Окно консольного клиента: TUI ставит заголовок «Conexy» (Bubble.WindowTitle). В Windows Terminal
    /// заголовок вкладки становится заголовком окна, поэтому ищем по тем же правилам, что и круг.
    /// </summary>
    private static IntPtr ConsoleWindow()
    {
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            var title = TitleOf(handle);
            if (title.Length == 0)
                return true;
            if (title.Equals(Bubble.WindowTitle, StringComparison.OrdinalIgnoreCase)
                || title.StartsWith(Bubble.WindowTitle + " ", StringComparison.OrdinalIgnoreCase)
                || title.StartsWith(Bubble.WindowTitle + " —", StringComparison.OrdinalIgnoreCase)
                || title.Contains(" — " + Bubble.WindowTitle, StringComparison.OrdinalIgnoreCase))
            {
                found = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool FocusConsole()
    {
        var console = ConsoleWindow();
        if (console == IntPtr.Zero)
            return false;
        FocusWindow(console);
        return true;
    }

    private static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;

    private static int ProcessIdOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    private static void FocusWindow(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindow(window, SwRestore);
        else
            ShowWindow(window, SwShow);
        // Windows не даёт фоновому процессу увести фокус без «своего» действия пользователя —
        // имитация нажатия Alt снимает это ограничение (тем же приёмом поднимает окно плавающий круг).
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(window);
    }

    private static string TitleOf(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static void Log(string message)
    {
        try
        {
            lock (LogSync)
            {
                Directory.CreateDirectory(ProjectStore.ConfigDir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал визуального окна — подсказка для разбора, его сбой не должен ломать клиент.
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint messageFilterMin, uint messageFilterMax, uint remove);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pointX;
        public int pointY;
    }
}

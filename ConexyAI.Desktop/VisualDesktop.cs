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
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    private static readonly object Sync = new();
    private static readonly object LogSync = new();
    private static int _hotkeyStarted;
    private static int _watcherStarted;
    private static int _consoleHidden;
    private static IntPtr _consoleWindow;
    private static Process? _browser;
    private static IntPtr _visualWindow;

    /// <summary>Профиль визуального окна: свой вход, история и настройки, отдельно от браузера пользователя.</summary>
    /// <summary>
    /// Профиль окна: вход, история и настройки визуального десктопа. По умолчанию лежит рядом с
    /// настройками клиента; CONEXY_VISUAL_PROFILE_DIR переносит его на другой диск (портативная
    /// установка, тесты в песочнице, где профиль пользователя недоступен).
    /// </summary>
    public static string ProfileDir =>
        Environment.GetEnvironmentVariable("CONEXY_VISUAL_PROFILE_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(ProjectStore.ConfigDir, "visual-desktop");

    public static string LogPath => Path.Combine(ProjectStore.ConfigDir, "visual-desktop.log");

    /// <summary>Визуальный десктоп есть только на Windows: остальным доступна веб-версия в браузере.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Удалось ли занять <see cref="HotkeyLabel"/> под переключение консоль/визуальный режим.</summary>
    public static bool HotkeyRegistered { get; private set; }

    /// <summary>Горячую клавишу вообще пытались зарегистрировать (интерактивный клиент это делает всегда).</summary>
    public static bool HotkeyStarted { get; private set; }

    /// <summary>
    /// Консоль свёрнута, потому что пользователь ушёл в визуальный десктоп. Пока это так, локальные
    /// подтверждения не задаются в скрытом терминале: задачи с сайта спрашивают в вебе, а ход из самой
    /// консоли временно возвращает окно терминала на экран.
    /// </summary>
    public static bool ConsoleHiddenByVisual => Volatile.Read(ref _consoleHidden) == 1;

    /// <summary>Открыто ли сейчас окно визуального десктопа.</summary>
    public static bool IsOpen => CurrentWindow() != IntPtr.Zero;

    /// <summary>
    /// Открывает визуальный десктоп или поднимает уже открытое окно. Возвращает строку для консоли:
    /// что именно произошло (включая запасной вариант «открыл в браузере»).
    /// </summary>
    public static async Task<string> OpenAsync(ServerClient? server, CancellationToken ct = default)
    {
        // Пока окно не открылось, фокус ещё может быть в терминале — фиксируем его handle заранее.
        CaptureConsoleWindow();
        var open = CurrentWindow();
        if (open != IntPtr.Zero)
        {
            FocusWindow(open);
            HideConsoleWindow();
            return "Визуальный десктоп уже открыт — поднял окно." + ReturnHint;
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
            // Терминал больше не нужен: пользователь работает в визуальном окне. Так подтверждения
            // задач с сайта не приходят в скрытую консоль (см. RoutingApprover).
            HideConsoleWindow();
            return "Открыл визуальный десктоп: это веб-версия в отдельном окне, вход — как на сайте. Терминал скрыт." + ToggleHint + ReturnHint;
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
                ShowConsole();
                return "Визуальный десктоп закрыт — вы в консоли.";
            }

            FocusWindow(window);
            HideConsoleWindow();
            return "Вернулся в визуальный десктоп." + ReturnHint;
        }

        return await OpenAsync(ServerClient.Current);
    }

    /// <summary>
    /// Режимы не живут одновременно: визуальное окно закрывается, консоль возвращается на экран.
    /// Вызывается командой /console, кнопкой «В консоль» в окне и горячей клавишей.
    /// </summary>
    public static bool ShowConsole()
    {
        CloseVisualWindow();
        return RestoreConsoleWindow();
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
        var hadWindow = CurrentWindow() != IntPtr.Zero;
        CloseVisualWindow();
        // Закрыли окно — пользователь остался без интерфейса: возвращаем консоль сразу.
        RestoreConsoleWindow();
        return hadWindow;
    }

    /// <summary>
    /// Закрывает окно визуального десктопа и дожидается выхода процесса: если просто свернуть,
    /// рядом останется живое окно, а пользователь просил переключаться между режимами, а не держать
    /// оба. Закрытие последнего окна приложения завершает и Chromium этого профиля.
    /// </summary>
    private static void CloseVisualWindow()
    {
        var window = CurrentWindow();
        if (window == IntPtr.Zero)
        {
            lock (Sync)
            {
                _browser?.Dispose();
                _browser = null;
            }
            return;
        }

        var pid = ProcessIdOf(window);
        PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        for (var attempt = 0; attempt < 30 && CurrentWindow() != IntPtr.Zero; attempt++)
            Thread.Sleep(100);
        if (CurrentWindow() != IntPtr.Zero)
            KillBrowser(pid);

        _visualWindow = IntPtr.Zero;
        lock (Sync)
        {
            _browser?.Dispose();
            _browser = null;
        }
    }

    /// <summary>
    /// Прячет окно консоли, пока пользователь работает в визуальном десктопе: он не должен видеть
    /// два интерфейса сразу. Скрытое окно остаётся живым — оно хранит сессию и горячую клавишу, —
    /// и возвращается кнопкой «В консоль», /console или Ctrl+Alt+Shift+V.
    /// </summary>
    private static void HideConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var console = ConsoleWindowForHide();
        if (console == IntPtr.Zero)
            return;
        _consoleWindow = console;
        // Сначала убираем окно с экрана (SW_HIDE), а если хост терминала не дал спрятать его —
        // сворачиваем: в обоих случаях пользователь видит только визуальный десктоп.
        ShowWindow(console, SwHide);
        Thread.Sleep(150);
        if (IsWindowVisible(console))
            ShowWindow(console, SwMinimize);
        Volatile.Write(ref _consoleHidden, 1);
        Log("console hidden: " + TitleOf(console) + " hwnd=" + console);
    }

    /// <summary>Возвращает на экран консоль, которую мы спрятали при переходе в визуальный режим.</summary>
    public static bool RestoreConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        // Скрытое окно не находится перебором видимых окон — держим его handle, а при протухании
        // ищем заново (например, после перезапуска терминала).
        var console = _consoleWindow != IntPtr.Zero && IsWindow(_consoleWindow) ? _consoleWindow : ConsoleWindow();
        if (console == IntPtr.Zero)
        {
            Volatile.Write(ref _consoleHidden, 0);
            return false;
        }

        _consoleWindow = console;
        ShowWindow(console, SwRestore);
        FocusWindow(console);
        Volatile.Write(ref _consoleHidden, 0);
        return true;
    }

    /// <summary>
    /// Следит за визуальным окном: если пользователь закрыл его крестиком, консоль возвращается на
    /// экран сама — без этого клиент остался бы в фоне со свёрнутым терминалом.
    /// </summary>
    public static void StartWatcher()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Interlocked.Exchange(ref _watcherStarted, 1) == 1)
            return;

        var thread = new Thread(ConsoleWatcherLoop)
        {
            IsBackground = true,
            Name = "conexy-visual-console-watch",
        };
        thread.Start();
    }

    private static void ConsoleWatcherLoop()
    {
        while (true)
        {
            Thread.Sleep(700);
            if (!ConsoleHiddenByVisual || CurrentWindow() != IntPtr.Zero)
                continue;
            // Окно визуального десктопа закрыто внешне — возвращаем терминал.
            if (RestoreConsoleWindow())
                Log("visual desktop closed; console restored");
        }
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
        _visualWindow = IntPtr.Zero;
        RestoreConsoleWindow();
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
            ConsoleHiddenByVisual
                ? "Терминал скрыт: вы в визуальном режиме. /console или кнопка «В консоль» закроют окно и вернут терминал."
                : "Терминал на экране.",
            "По умолчанию при запуске: " + (ClientConfig.LoadVisualDefault() ? "визуальный десктоп" : "консоль") +
            " (/visual default — переключить).",
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

    /// <summary>Как вернуться из визуального окна в терминал — показывается после открытия окна.</summary>
    public const string ReturnHint = " Вернуться в консоль: /console или кнопка «В консоль» в окне.";

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
        // Handle найденного один раз окна — самый надёжный признак: страница может сменить
        // document.title (до деплоя новой версии веба), и поиск по заголовку перестал бы её находить.
        if (_visualWindow != IntPtr.Zero && IsWindow(_visualWindow) && IsWindowVisible(_visualWindow))
            return _visualWindow;

        var byTitle = WindowHandle();
        if (byTitle != IntPtr.Zero)
            return _visualWindow = byTitle;

        var launched = LaunchedWindowHandle();
        if (launched != IntPtr.Zero)
            return _visualWindow = launched;

        var provisional = ProvisionalTitle(ServerClient.Current?.BaseUrl);
        var provisionalWindow = provisional is null ? IntPtr.Zero : FindWindow(null, provisional);
        if (provisionalWindow != IntPtr.Zero)
            _visualWindow = provisionalWindow;
        return provisionalWindow;
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
    /// Запоминает окно терминала заранее — пока пользователь ещё в консоли. После запуска окна
    /// визуального десктопа Chromium забирает фокус, а у Windows Terminal несколько top-level окон;
    /// поэтому handle видимого терминала фиксируем до открытия окна (при /visual, --visual и старте).
    /// </summary>
    public static void CaptureConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (_consoleWindow != IntPtr.Zero && IsWindow(_consoleWindow) && IsWindowVisible(_consoleWindow))
            return;

        // 1. Активное окно — если это терминал клиента (заголовок ставит сам TUI: «Conexy»).
        //    Проверяем именно заголовок, а не процесс: окно плавающего круга тоже живёт в
        //    powershell.exe, и раньше проверка «процесс — терминал» прятала круг вместо консоли.
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && IsWindowVisible(foreground) && IsConsoleWindowTitle(TitleOf(foreground)))
        {
            _consoleWindow = foreground;
            Log("console captured from foreground: " + DescribeWindow(foreground));
            return;
        }

        // 2. Видимое окно терминала с заголовком клиента (активная вкладка Windows Terminal).
        var byTitle = ConsoleWindow();
        if (byTitle != IntPtr.Zero)
        {
            _consoleWindow = byTitle;
            Log("console captured by title: " + DescribeWindow(byTitle));
            return;
        }

        // 3. Классический conhost: GetConsoleWindow даёт само окно терминала, если у него есть
        //    заголовок клиента. Служебное окно консоли (пустой заголовок) не берём.
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero)
        {
            var root = GetAncestor(console, GaRoot);
            if (root != IntPtr.Zero)
                console = root;
            if (IsWindowVisible(console) && IsConsoleWindowTitle(TitleOf(console)))
            {
                _consoleWindow = console;
                Log("console captured by GetConsoleWindow: " + DescribeWindow(console));
                return;
            }
        }

        Log("console window not found; it will not be hidden");
    }

    private static string DescribeWindow(IntPtr window) =>
        "'" + TitleOf(window) + "' hwnd=" + window;
    /// <summary>
    /// Окно терминала, в котором запущен клиент. Сохранённый handle — приоритет: он взят до того,
    /// как фокус забрал визуальный десктоп.
    /// </summary>
    private static IntPtr ConsoleWindowForHide()
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;
        if (_consoleWindow != IntPtr.Zero && IsWindow(_consoleWindow) && IsWindowVisible(_consoleWindow))
            return _consoleWindow;

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && IsConsoleWindowTitle(TitleOf(foreground)))
            return foreground;

        // Резерв: активное окно не терминал (фокус уже у браузера) — ищем по заголовку, а затем
        // пробуем корень окна консоли текущего процесса (только если у него заголовок клиента).
        var found = ConsoleWindow();
        if (found != IntPtr.Zero)
            return found;
        var console = GetConsoleWindow();
        if (console == IntPtr.Zero)
            return IntPtr.Zero;
        var root = GetAncestor(console, GaRoot);
        if (root != IntPtr.Zero)
            console = root;
        return IsWindowVisible(console) && IsConsoleWindowTitle(TitleOf(console)) ? console : IntPtr.Zero;
    }

    private static bool IsConsoleWindowTitle(string title) =>
        title.Length > 0
        && (title.Equals(Bubble.WindowTitle, StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(Bubble.WindowTitle + " ", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(Bubble.WindowTitle + " —", StringComparison.OrdinalIgnoreCase)
            || title.Contains(" — " + Bubble.WindowTitle, StringComparison.OrdinalIgnoreCase));

    private static IntPtr ConsoleWindow()
    {
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            if (IsConsoleWindowTitle(TitleOf(handle)))
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
    private static extern bool IsWindow(IntPtr window);

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

    private const uint GaRoot = 2;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();



    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

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

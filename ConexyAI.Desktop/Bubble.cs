using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConexyAI.Desktop;

/// <summary>
/// FLOAT_BUBBLE: маленький помощник на рабочем столе — чёрный круг с логотипом, всегда поверх других окон.
/// Висит, пока запущен Conexy (окно или фоновый демон): клик открывает окно Conexy (или поднимает уже
/// открытое), на круг можно перетащить файлы — они станут вложениями, сам круг перетаскивается мышью.
/// <para>Клиент консольный, поэтому окно рисует PowerShell + WinForms. Единственный экземпляр охраняет
/// мьютекс внутри скрипта: сколько бы процессов клиента ни запустили круг, он один.</para>
/// </summary>
public static class Bubble
{
    /// <summary>Держится, пока открыто интерактивное окно клиента.</summary>
    public const string InteractiveMutexName = @"Local\ConexyAI-Interactive";

    /// <summary>Событие «подними окно»: круг не запускает второй клиент, а просит уже открытый показаться.</summary>
    public const string ShowWindowEventName = @"Local\ConexyAI-Show";

    private static Process? _process;
    private static readonly object Sync = new();

    public static string LogPath => Path.Combine(ProjectStore.ConfigDir, "bubble.log");

    private const string Script = """
        param([string]$Exe, [string]$ShowEvent, [string]$InteractiveMutex, [string]$Log)

        $ErrorActionPreference = 'Stop'
        function Log($m) {
            try { Add-Content -LiteralPath $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $m) } catch { }
        }

        try {
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            [System.Windows.Forms.Application]::EnableVisualStyles()
        } catch {
            Log ('init failed: ' + $_.Exception.Message)
            exit 1
        }

        # Единственный круг на всю сессию: второй экземпляр молча выходит.
        $own = New-Object System.Threading.Mutex($false, 'Local\ConexyAI-Bubble')
        if (-not $own.WaitOne(0)) { exit 0 }

        $size = 56
        $form = New-Object System.Windows.Forms.Form
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.ShowInTaskbar = $false
        $form.TopMost = $true
        $form.BackColor = [System.Drawing.Color]::FromArgb(18, 18, 18)
        $form.ClientSize = New-Object System.Drawing.Size($size, $size)
        $form.Text = 'Conexy'

        # Круглое окно: регион-эллипс, иначе остались бы тёмные углы.
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse(0, 0, $size - 1, $size - 1)
        $form.Region = New-Object System.Drawing.Region($path)

        # Логотип — иконка самого conexy.exe.
        try { $form.Tag = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe) } catch { Log ('icon: ' + $_.Exception.Message) }

        $form.Add_Paint({
            param($s, $e)
            $e.Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(18, 18, 18))
            $e.Graphics.FillEllipse($brush, 0, 0, $s.Width - 1, $s.Height - 1)
            $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 70, 70)), 1
            $e.Graphics.DrawEllipse($pen, 0, 0, $s.Width - 2, $s.Height - 2)
            if ($s.Tag -ne $null) {
                $icon = New-Object System.Drawing.Icon -ArgumentList $s.Tag, 34, 34
                $e.Graphics.DrawIcon($icon, 11, 11)
            }
            $brush.Dispose()
            $pen.Dispose()
        })

        # В правом нижнем углу, над панелью задач; положение запоминается между запусками.
        $posFile = Join-Path (Split-Path $Log) 'bubble.pos'
        $placed = $false
        try {
            if (Test-Path -LiteralPath $posFile) {
                $saved = (Get-Content -LiteralPath $posFile -Raw).Trim() -split ','
                $x = [int]$saved[0]; $y = [int]$saved[1]
                $form.Location = New-Object System.Drawing.Point $x, $y
                $placed = $true
            }
        } catch { $placed = $false }
        if (-not $placed) {
            $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
            $form.Location = New-Object System.Drawing.Point (($area.Right - $size - 48)), ($area.Bottom - $size - 96)
        }

        $dragging = $false
        $moved = $false
        $start = New-Object System.Drawing.Point 0, 0

        $form.Add_MouseDown({
            param($s, $e)
            if ($e.Button -ne [System.Windows.Forms.MouseButtons]::Left) { return }
            $script:dragging = $true
            $script:moved = $false
            $script:start = [System.Windows.Forms.Cursor]::Position
        })
        $form.Add_MouseMove({
            param($s, $e)
            if (-not $script:dragging) { return }
            $now = [System.Windows.Forms.Cursor]::Position
            $dx = $now.X - $script:start.X
            $dy = $now.Y - $script:start.Y
            if ([Math]::Abs($dx) -lt 4 -and [Math]::Abs($dy) -lt 4) { return }
            $script:moved = $true
            $s.Left += $dx
            $s.Top += $dy
            $script:start = $now
        })
        $form.Add_MouseUp({
            param($s, $e)
            if ($e.Button -ne [System.Windows.Forms.MouseButtons]::Left) { return }
            $script:dragging = $false
            if ($script:moved) {
                try { Set-Content -LiteralPath $posFile -Value ($s.Left.ToString() + ',' + $s.Top.ToString()) -Encoding ascii } catch { }
                return
            }
            # Окно Conexy открыто — просим его показаться; нет — запускаем клиент.
            $running = $false
            try {
                $m = [System.Threading.Mutex]::OpenExisting($InteractiveMutex)
                $m.Dispose()
                $running = $true
            } catch { $running = $false }
            if ($running) {
                try {
                    $ev = [System.Threading.EventWaitHandle]::OpenExisting($ShowEvent)
                    $ev.Set() | Out-Null
                    $ev.Dispose()
                } catch { Log ('show event: ' + $_.Exception.Message) }
            } else {
                try { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) } catch { Log ('start: ' + $_.Exception.Message) }
            }
        })

        # Файлы, брошенные на круг, становятся вложениями новой сессии.
        $form.AllowDrop = $true
        $form.Add_DragEnter({
            param($s, $e)
            if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) {
                $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy
            } else {
                $e.Effect = [System.Windows.Forms.DragDropEffects]::None
            }
        })
        $form.Add_DragDrop({
            param($s, $e)
            $files = $e.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop)
            if ($files -eq $null) { return }
            $list = ($files | ForEach-Object { '"' + $_ + '"' }) -join ' '
            try { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList ('--attach ' + $list) }
            catch { Log ('drop: ' + $_.Exception.Message) }
        })

        # Круг всегда сверху: некоторые приложения и полноэкранные окна снимают флаг — возвращаем.
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 1500
        $timer.Add_Tick({
            if (-not $script:form.TopMost) { $script:form.TopMost = $true }
        })
        $script:form = $form
        $timer.Start()

        Log ('shown at ' + $form.Location + ' exe=' + $Exe)
        [System.Windows.Forms.Application]::Run($form)
        """;

    /// <summary>Показывает круг (если ещё не показан). Можно звать из любого процесса клиента.</summary>
    public static void Show()
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (Sync)
        {
            if (_process is { HasExited: false })
                return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return;

            var scriptPath = Path.Combine(ProjectStore.ConfigDir, "bubble.ps1");
            try
            {
                Directory.CreateDirectory(ProjectStore.ConfigDir);
                File.WriteAllText(scriptPath, Script);
                var shell = PowershellPath();
                var psi = new ProcessStartInfo(shell)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in new[]
                         {
                             "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                             "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                             "-Exe", exe, "-ShowEvent", ShowWindowEventName,
                             "-InteractiveMutex", InteractiveMutexName, "-Log", LogPath,
                         })
                    psi.ArgumentList.Add(arg);
                _process = Process.Start(psi);
                Log($"bubble launched: pid={_process?.Id} shell={shell}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                _process = null;
                Log("bubble start failed: " + ex.Message);
            }
        }
    }

    /// <summary>Убирает круг (клиент закрывается).</summary>
    public static void Hide()
    {
        lock (Sync)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            _process = null;
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(ProjectStore.ConfigDir);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Windows PowerShell 5.1 есть на любой Windows; pwsh — запасной вариант.</summary>
    private static string PowershellPath()
    {
        var legacy = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(legacy) ? legacy : "pwsh.exe";
    }

    // ── Связь с открытым окном ────────────────────────────────────────────────

    /// <summary>
    /// Объявляет это окно интерактивным: по мьютексу круг понимает, что клиент уже открыт,
    /// а по событию — когда его просят показаться.
    /// </summary>
    public static void MarkInteractive()
    {
        try
        {
            // Не освобождаем: мьютекс должен жить, пока жив процесс окна.
            _interactive = new Mutex(initiallyOwned: true, InteractiveMutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _interactive = null;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        BringConsoleToFront();
                    }
                    catch (ObjectDisposedException) { return; }
                }
            })
            { IsBackground = true, Name = "conexy-show-window" };
            thread.Start();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _showEvent = null;
        }
    }

    /// <summary>Поднимает окно консоли клиента (круг попросил показаться).</summary>
    private static void BringConsoleToFront()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd == IntPtr.Zero)
            return;
        ShowWindow(hwnd, 9);   // SW_RESTORE
        ShowWindow(hwnd, 5);   // SW_SHOW
        SetForegroundWindow(hwnd);
    }

    // ReSharper disable once NotAccessedField.Local — держим ссылки, чтобы мьютекс и событие не освободились.
    private static Mutex? _interactive;
    private static EventWaitHandle? _showEvent;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

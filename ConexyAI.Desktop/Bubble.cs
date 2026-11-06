using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ConexyAI.Desktop;

/// <summary>
/// FLOAT_BUBBLE: маленький помощник на рабочем столе — облачко-компаньон внутри чёрного круга, всегда
/// поверх других окон. Висит, пока запущен Conexy (окно или фоновый демон): клик открывает окно Conexy
/// (или поднимает уже открытое), на круг можно перетащить файлы — они станут вложениями, сам круг
/// перетаскивается мышью. Облачко «живёт»: левитирует, моргает и следит глазами за курсором; когда
/// агенту нужно подтверждение, в круге загорается значок уведомления.
/// <para>Клиент консольный, поэтому окно рисует PowerShell + WinForms. Единственный экземпляр охраняет
/// мьютекс внутри скрипта: сколько бы процессов клиента ни запустили круг, он один.</para>
/// </summary>
public static class Bubble
{
    /// <summary>Держится, пока открыто интерактивное окно клиента.</summary>
    public const string InteractiveMutexName = @"Local\ConexyAI-Interactive";

    /// <summary>Событие «подними окно»: круг не запускает второй клиент, а просит уже открытый показаться.</summary>
    public const string ShowWindowEventName = @"Local\ConexyAI-Show";

    /// <summary>Событие «закройся»: нажатие в трее закрывает клиент и убирает круг.</summary>
    public const string QuitEventName = @"Local\ConexyAI-Quit";

    private static Process? _process;
    private static readonly object Sync = new();

    public static string LogPath => Path.Combine(ProjectStore.ConfigDir, "bubble.log");

    /// <summary>Сколько подтверждений сейчас ждут ответа: по этому файлу круг показывает значок.</summary>
    public static string ApprovalFile => Path.Combine(ProjectStore.ConfigDir, "approvals.pending");

    private const string Script = """
        param([string]$Exe, [string]$ShowEvent, [string]$QuitEvent, [string]$InteractiveMutex, [string]$Log, [string]$ApprovalFile)

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

        $size = 64

        # <companion-draw>
        function Add-Capsule($path, $x, $y, $w, $h) {
            $d = [Math]::Min($w, $h)
            if ($h -le $w) {
                $path.AddEllipse($x, $y, $w, $h)
            } else {
                $path.AddArc($x, $y, $w, $d, 180, 180)
                $path.AddArc($x, $y + $h - $d, $w, $d, 0, 180)
                $path.CloseFigure()
            }
        }

        # Компаньон: белое облачко с чёрными глазами внутри круга. $lift — левитация, $eyeDx/$eyeDy — взгляд,
        # $blink — прищур, $badge — значок «нужно подтверждение».
        function Draw-Companion($g, $w, $h, $lift, $eyeDx, $eyeDy, $blink, $badge, $pulse) {
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # VISIBLE_CIRCLE: рисуем по МЕНЬШЕЙ стороне — ровно та же окружность, что и Region, поэтому
            # облако всегда в центре видимого круга, даже если окно посчитало себя больше.
            $d = [Math]::Min($w, $h)
            $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)
            $g.FillRectangle($bg, 0, 0, $w, $h)
            $g.FillEllipse($bg, 0, 0, ($d - 1), ($d - 1))
            $bg.Dispose()

            $cx = $d / 2.0
            $cy = $d / 2.0 + $lift

            # Силуэт облака: три пуховые шапки сверху и общее основание. Один путь с Winding = объединение.
            $cloud = New-Object System.Drawing.Drawing2D.GraphicsPath
            $cloud.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding
            $cloud.AddEllipse(($cx - 21.0), ($cy - 5.0), 17.0, 17.0)
            $cloud.AddEllipse(($cx - 11.0), ($cy - 16.0), 23.0, 23.0)
            $cloud.AddEllipse(($cx + 5.0), ($cy - 6.0), 17.0, 17.0)
            $cloud.AddEllipse(($cx - 20.0), ($cy + 0.5), 40.0, 16.0)
            $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
            $g.FillPath($white, $cloud)
            $white.Dispose()
            $cloud.Dispose()

            # Глаза: чёрные капсулы; при моргании превращаются в тонкие полоски.
            $eyeW = 6.5
            $eyeH = if ($blink) { 2.2 } else { 11.0 }
            $eyeTop = $cy - 2.5 + $eyeDy
            $eyes = @(($cx - 9.0 + $eyeDx), ($cx + 2.5 + $eyeDx))
            $black = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)
            foreach ($ex in $eyes) {
                $p = New-Object System.Drawing.Drawing2D.GraphicsPath
                Add-Capsule $p $ex $eyeTop $eyeW $eyeH
                $g.FillPath($black, $p)
                $p.Dispose()
            }
            $black.Dispose()

            # Значок «нужно подтвердить»: пульсирующая янтарная точка с восклицательным знаком.
            # BADGE_INSIDE: держим его внутри видимого круга — у края он обрезался дугой.
            if ($badge) {
                $alpha = [int](180 + 75 * $pulse)
                $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 245, 166, 35))
                $bx = $d - 23.0
                $by = $d - 53.0
                $g.FillEllipse($amber, $bx, $by, 15.0, 15.0)
                $amber.Dispose()
                $mark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)
                $g.FillRectangle($mark, ($bx + 6.4), ($by + 3.0), 2.3, 5.6)
                $g.FillRectangle($mark, ($bx + 6.4), ($by + 10.1), 2.3, 2.1)
                $mark.Dispose()
            }
        }
        # </companion-draw>

        $form = New-Object System.Windows.Forms.Form
        $form.FormBorderStyle = 'None'
        # DPI_FIX: без этого окно само меняло размер на экранах с масштабом 125–150%, а круглая область
        # оставалась прежней — облако рисовалось правее видимого круга и обрезалось.
        # FLICKER_FIX: без двойной буферзации перерисовка 20 раз в секунду сильно мерцала.
        $doubleBuffered = [System.Windows.Forms.Form].GetProperty('DoubleBuffered',
            [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic)
        $doubleBuffered.SetValue($form, $true)
        $form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
        $form.AutoScaleDimensions = New-Object System.Drawing.SizeF(96, 96)
        $form.StartPosition = 'Manual'
        $form.ShowInTaskbar = $false
        $form.TopMost = $true
        $form.BackColor = [System.Drawing.Color]::Black
        $form.ClientSize = New-Object System.Drawing.Size($size, $size)
        $form.Text = 'Conexy'

        # Круглое окно: регион-эллипс по фактическому размеру клиента (и при любом изменении).
        function Update-Shape {
            $d = [Math]::Min($script:form.ClientSize.Width, $script:form.ClientSize.Height)
            if ($d -lt 8) { return }
            $p = New-Object System.Drawing.Drawing2D.GraphicsPath
            $p.AddEllipse(0, 0, ($d - 1), ($d - 1))
            try { $script:form.Region = New-Object System.Drawing.Region($p) } catch { }
            $p.Dispose()
        }

        $script:phase = 0.0
        $script:eyeDx = 0.0
        $script:eyeDy = 0.0
        $script:blink = $false
        $script:badge = $false
        $script:pulse = 0.0
        $script:nextBlink = (Get-Date).AddMilliseconds(1500)
        $script:blinkUntil = [DateTime]::MinValue
        $script:lastBadgeCheck = [DateTime]::MinValue

        $form.Add_Paint({
            param($s, $e)
            $lift = [Math]::Sin($script:phase) * 2.6
            # PAINT_CENTER: размер берём из клиентской области — так облако всегда точно в центре круга.
            $w = $s.ClientSize.Width
            $h = $s.ClientSize.Height
            Draw-Companion $e.Graphics $w $h $lift $script:eyeDx $script:eyeDy $script:blink $script:badge $script:pulse
        })

        $posFile = Join-Path (Split-Path $Log) 'bubble.pos'
        $placed = $false
        try {
            if (Test-Path -LiteralPath $posFile) {
                $saved = (Get-Content -LiteralPath $posFile -Raw).Trim() -split ','
                $form.Location = New-Object System.Drawing.Point ([int]$saved[0]), ([int]$saved[1])
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

        # Общие действия: открыть окно и закрыть клиент целиком (трей и клик по кругу).
        function Open-Conexy {
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
        }

        function Close-Conexy {
            try {
                $ev = [System.Threading.EventWaitHandle]::OpenExisting($QuitEvent)
                $ev.Set() | Out-Null
                $ev.Dispose()
            } catch { }
            # Клиент может быть занят — через полторы секунды добиваем оставшиеся процессы.
            Start-Sleep -Milliseconds 1500
            try { Get-Process -Name conexy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch { }
            Log 'closed from tray'
        }

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
            Open-Conexy
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

        # Живость: левитация, случайные моргания, взгляд за курсором, значок подтверждения.
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 50
        $timer.Add_Tick({
            $now = Get-Date
            $script:phase += 0.11

            if ($now -gt $script:nextBlink) {
                $script:blinkUntil = $now.AddMilliseconds(130)
                $script:nextBlink = $now.AddMilliseconds((Get-Random -Minimum 2200 -Maximum 5200))
            }
            $script:blink = $now -lt $script:blinkUntil

            # Глаза следят за курсором, когда он рядом с кругом.
            try {
                $p = [System.Windows.Forms.Cursor]::Position
                $local = $script:form.PointToClient((New-Object System.Drawing.Point $p.X, $p.Y))
                $dx = $local.X - ($script:form.ClientSize.Width / 2.0)
                $dy = $local.Y - ($script:form.ClientSize.Height / 2.0)
                $dist = [Math]::Sqrt($dx * $dx + $dy * $dy)
                if ($dist -gt 1 -and $dist -lt 180) {
                    $script:eyeDx = [Math]::Max(-2.2, [Math]::Min(2.2, $dx / 34.0))
                    $script:eyeDy = [Math]::Max(-1.6, [Math]::Min(1.6, $dy / 44.0))
                } else {
                    $script:eyeDx = 0.0
                    $script:eyeDy = 0.0
                }
            } catch { }

            # Значок подтверждения: файл со счётчиком пишет клиент, пока ждёт ответа.
            if (($now - $script:lastBadgeCheck).TotalMilliseconds -gt 400) {
                $script:lastBadgeCheck = $now
                $pending = $false
                try {
                    if (Test-Path -LiteralPath $ApprovalFile) {
                        $raw = (Get-Content -LiteralPath $ApprovalFile -Raw).Trim()
                        $pending = ($raw.Length -gt 0 -and [int]$raw -gt 0)
                    }
                } catch { $pending = $false }
                $script:badge = $pending
            }
            $script:pulse = ([Math]::Sin($now.TimeOfDay.TotalSeconds * 4.0) + 1.0) / 2.0

            if (-not $script:form.TopMost) { $script:form.TopMost = $true }
            $script:form.Invalidate()
        })
        $script:form = $form
        Update-Shape
        $form.Add_Resize({ Update-Shape })
        # SIZE_ON_SHOW: при показе WinForms сам растягивал окно до 136x64 (круг занимал левую часть,
        # а облако рисовалось в центре широкого окна — «уезжало вправо»). Задаём размер после показа.
        $form.Add_Shown({
            $script:form.ClientSize = New-Object System.Drawing.Size($size, $size)
            Update-Shape
            $script:form.Invalidate()
            Log ('client=' + $script:form.ClientSize.Width + 'x' + $script:form.ClientSize.Height)
        })
        $timer.Start()

        # Значок в трее («скрытые значки»): там Conexy можно открыть или закрыть полностью.
        try {
            $trayIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe)
            $tray = New-Object System.Windows.Forms.NotifyIcon
            $tray.Icon = if ($trayIcon) { $trayIcon } else { [System.Drawing.SystemIcons]::Application }
            $tray.Text = 'Conexy'
            $menu = New-Object System.Windows.Forms.ContextMenuStrip
            $itemOpen = $menu.Items.Add('Открыть Conexy')
            $itemOpen.Add_Click({ Open-Conexy })
            $itemQuit = $menu.Items.Add('Закрыть Conexy')
            $itemQuit.Add_Click({ Close-Conexy; $script:tray.Visible = $false; $script:form.Close() })
            $tray.ContextMenuStrip = $menu
            $tray.Add_DoubleClick({ Open-Conexy })
            $script:tray = $tray
            $tray.Visible = $true
        } catch {
            Log ('tray: ' + $_.Exception.Message)
        }

        $form.Add_FormClosed({
            try { $script:tray.Visible = $false; $script:tray.Dispose() } catch { }
        })

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
                // PS_UTF8_BOM: Windows PowerShell 5.1 читает .ps1 без BOM как ANSI — русский текст в меню
                // превращался в кракозябры и ломал разбор скрипта.
                File.WriteAllText(scriptPath, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                var psi = new ProcessStartInfo(PowershellPath())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in new[]
                         {
                             "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                             "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                             "-Exe", exe, "-ShowEvent", ShowWindowEventName,
                             "-QuitEvent", QuitEventName,
                             "-InteractiveMutex", InteractiveMutexName, "-Log", LogPath,
                             "-ApprovalFile", ApprovalFile,
                         })
                    psi.ArgumentList.Add(arg);
                _process = Process.Start(psi);
                Log($"bubble launched: pid={_process?.Id}");
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

    /// <summary>
    /// Слушает команду «закрыть» из трея: по ней процесс клиента завершается, и круг с клиентом исчезают.
    /// </summary>
    public static void ListenForQuit(Action onQuit)
    {
        try
        {
            _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            var thread = new Thread(() =>
            {
                try
                {
                    _quitEvent.WaitOne();
                }
                catch (ObjectDisposedException) { return; }
                onQuit();
            })
            { IsBackground = true, Name = "conexy-quit" };
            thread.Start();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _quitEvent = null;
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

    // ReSharper disable once NotAccessedField.Local — держим ссылки, чтобы мьютекс и события не освободились.
    private static Mutex? _interactive;
    private static EventWaitHandle? _showEvent;
    private static EventWaitHandle? _quitEvent;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

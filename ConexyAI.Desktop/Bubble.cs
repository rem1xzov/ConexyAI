using System.Diagnostics;

namespace ConexyAI.Desktop;

/// <summary>
/// FLOAT_BUBBLE: пока Conexy работает в фоне (демон), на рабочем столе висит чёрный кружок с логотипом —
/// поверх всех окон. Клик открывает окно Conexy, на кружок можно перетащить файлы (они станут вложениями),
/// сам кружок перетаскивается мышью. Пока открыто окно Conexy, кружок прячется.
/// <para>Клиент консольный, поэтому окно рисует PowerShell + WinForms; процесс живёт, пока жив демон.</para>
/// </summary>
public static class Bubble
{
    /// <summary>Создаёт интерактивное окно: пока мьютекс жив, кружок скрыт.</summary>
    public const string InteractiveMutexName = @"Local\ConexyAI-Interactive";

    private static Process? _process;

    private const string Script = """
        param([string]$Exe, [string]$MutexName)

        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $ErrorActionPreference = 'Stop'
        [System.Windows.Forms.Application]::EnableVisualStyles()

        $size = 64
        $form = New-Object System.Windows.Forms.Form
        $form.FormBorderStyle = 'None'
        $form.StartPosition = 'Manual'
        $form.ShowInTaskbar = $false
        $form.TopMost = $true
        $form.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 24)
        $form.ClientSize = New-Object System.Drawing.Size($size, $size)

        # Круглое окно: регион-эллипс, иначе останутся чёрные углы.
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse(0, 0, $size - 1, $size - 1)
        $form.Region = New-Object System.Drawing.Region($path)

        # Логотип — иконка самого conexy.exe, вписанная в круг.
        $logo = $null
        try { $logo = [System.Drawing.Icon]::ExtractAssociatedIcon($Exe) } catch { }
        $form.Tag = $logo

        $form.Add_Paint({
            param($s, $e)
            $e.Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(24, 24, 24))
            $e.Graphics.FillEllipse($brush, 0, 0, $s.Width - 1, $s.Height - 1)
            if ($s.Tag -ne $null) {
                $icon = New-Object System.Drawing.Icon -ArgumentList $s.Tag, 42, 42
                $e.Graphics.DrawIcon($icon, 11, 11)
            }
            $brush.Dispose()
        })

        # Позиция: правый нижний угол, над панелью задач. Дальше её помнит сам пользователь.
        $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        $form.Location = New-Object System.Drawing.Point (($area.Right - $size - 48), ($area.Bottom - $size - 120))

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
            # Короткое нажатие без сдвига — открыть Conexy; перетаскивание — только перенос кружка.
            if (-not $script:moved) {
                try { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) } catch { }
            }
        })

        # Файлы, брошенные на кружок, становятся вложениями новой сессии.
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
            # Пути с пробелами — в кавычках, иначе --attach получит обрывки.
            $list = ($files | ForEach-Object { '"' + $_ + '"' }) -join ' '
            try {
                Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -ArgumentList ('--attach ' + $list)
            } catch { }
        })

        # Пока открыто окно Conexy (мьютекс жив), кружок прячется; закрыли — возвращается поверх окон.
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 1000
        $timer.Add_Tick({
            $open = $false
            try {
                $m = [System.Threading.Mutex]::OpenExisting($MutexName)
                $m.Dispose()
                $open = $true
            } catch { $open = $false }
            if ($open) {
                if ($script:form.Visible) { $script:form.Hide() }
            } elseif (-not $script:form.Visible) {
                $script:form.Show()
                $script:form.TopMost = $true
            }
        })
        $script:form = $form
        $timer.Start()

        [System.Windows.Forms.Application]::Run($form)
        """;

    /// <summary>Показывает кружок на рабочем столе (фон). Повторный вызов ничего не делает.</summary>
    public static void Show()
    {
        if (!OperatingSystem.IsWindows() || _process is { HasExited: false })
            return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return;

        var scriptPath = Path.Combine(ProjectStore.ConfigDir, "bubble.ps1");
        try
        {
            Directory.CreateDirectory(ProjectStore.ConfigDir);
            // BUBBLE_TOOLTIP: скрипт лежит в папке настроек — не плодим временные файлы на каждый запуск.
            File.WriteAllText(scriptPath, Script);
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                         "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                         "-Exe", exe, "-MutexName", InteractiveMutexName,
                     })
                psi.ArgumentList.Add(arg);
            _process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _process = null;
        }
    }

    /// <summary>Убирает кружок (демон останавливается).</summary>
    public static void Hide()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        _process = null;
    }

    /// <summary>Держит мьютекс, пока открыто интерактивное окно: по нему кружок понимает, что окно есть.</summary>
    public static void MarkInteractive()
    {
        try
        {
            // Не освобождаем и не Dispose: мьютекс должен жить, пока жив процесс окна.
            _interactive = new Mutex(initiallyOwned: true, InteractiveMutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _interactive = null;
        }
    }

    // ReSharper disable once NotAccessedField.Local — держим ссылку, чтобы мьютекс не освободился.
    private static Mutex? _interactive;
}

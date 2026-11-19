using System.Diagnostics;

namespace ConexyAI.Desktop;

// AGENT_CURSOR: добавлено 2026-11-19 — оверлей с разноцветными курсорами агентов поверх рабочего стола.
// Отдельный powershell.exe рисует прозрачное click-through окно на весь виртуальный экран и читает
// %APPDATA%\ConexyAI\cursors.txt (его пишет CursorFeed). Своя мышь у пользователя одна, поэтому оверлей —
// единственный способ видеть, что по экрану работают несколько агентов и кто именно где.
public static class CursorOverlay
{
    private static Process? _process;
    private static readonly object Sync = new();

    /// <summary>Запускает оверлей один раз; ошибки не мешают работе клиента.</summary>
    public static void Start()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (Sync)
        {
            if (_process is { HasExited: false })
                return;

            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConexyAI");
                Directory.CreateDirectory(directory);
                var scriptPath = Path.Combine(directory, "cursor-overlay.ps1");
                File.WriteAllText(scriptPath, Script);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _process = Process.Start(startInfo);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                _process = null;
            }
        }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Процесс уже завершился.
            }
            finally
            {
                _process = null;
            }
        }
    }

    // Скрипт намеренно без Add-Type-компиляции C#: только WinForms/GDI+ и два P/Invoke.
    private const string Script =
        """
        $ErrorActionPreference = 'SilentlyContinue'
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class CxWin { [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i); [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v); }'

        $path = Join-Path $env:APPDATA 'ConexyAI\cursors.txt'
        $form = New-Object System.Windows.Forms.Form
        $form.FormBorderStyle = 'None'
        $form.ShowInTaskbar = $false
        $form.TopMost = $true
        $form.StartPosition = 'Manual'
        $form.BackColor = [System.Drawing.Color]::Fuchsia
        $form.TransparencyKey = [System.Drawing.Color]::Fuchsia
        $form.Bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $null = $form.Handle
        $ex = [CxWin]::GetWindowLong($form.Handle, -20)
        [CxWin]::SetWindowLong($form.Handle, -20, $ex -bor 0x20 -bor 0x80000 -bor 0x80 -bor 0x08000000) | Out-Null

        $script:cursors = @()
        $script:last = ''

        $refresh = {
            if (-not (Test-Path -LiteralPath $path)) {
                $script:cursors = @()
                return
            }
            $raw = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
            if ($null -eq $raw) { $raw = '' }
            if ($raw -eq $script:last) { return }
            $script:last = $raw
            $script:cursors = @($raw -split "`r?`n" | Where-Object { $_ -match '\|' } | ForEach-Object {
                $p = $_ -split '\|'
                [pscustomobject]@{ Color = $p[0]; X = [int]$p[1]; Y = [int]$p[2]; Role = $p[3]; Changed = $p[4] }
            })
            $form.Invalidate()
        }

        $form.Add_Paint({
            param($sender, $e)
            $g = $e.Graphics
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            foreach ($c in $script:cursors) {
                $color = [System.Drawing.ColorTranslator]::FromHtml($c.Color)
                $pen = New-Object System.Drawing.Pen($color, 2.5)
                $brush = New-Object System.Drawing.SolidBrush($color)
                $font = New-Object System.Drawing.Font('Segoe UI', 9)
                $g.DrawEllipse($pen, ($c.X - 10), ($c.Y - 10), 20, 20)
                $g.FillEllipse($brush, ($c.X - 3), ($c.Y - 3), 6, 6)
                $label = $c.Role
                if ($c.Changed -eq '0') { $label = $label + ' ·' }
                $g.DrawString($label, $font, $brush, ($c.X + 13), ($c.Y + 5))
                $pen.Dispose(); $brush.Dispose(); $font.Dispose()
            }
        })

        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 150
        $timer.Add_Tick({ & $refresh })
        $timer.Start()
        & $refresh
        [System.Windows.Forms.Application]::Run($form)
        """;
}

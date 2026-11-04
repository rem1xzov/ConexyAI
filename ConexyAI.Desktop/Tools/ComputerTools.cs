using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConexyAI.Desktop.Tools;

/// <summary>
/// PC_CONTROL: агент управляет ПК как человек — смотрит на экран, двигает мышь, кликает, печатает,
/// нажимает сочетания клавиш, открывает программы и сайты. Пользователь видит всё на своём экране.
/// <para>
/// Режим «полный доступ» (<c>/control on</c> или <c>conexy control on</c>) — действия без вопросов.
/// Без него каждое действие подтверждает человек. Аварийная остановка в любом режиме: увести курсор
/// в левый верхний угол экрана — следующее действие агента будет отклонено.
/// </para>
/// </summary>
public sealed class ComputerTools
{
    private const int MaxSide = 1600;
    private const int MaxTextLength = 4000;

    private readonly ICommandApprover _approver;
    private readonly string _root;
    private readonly Action<string> _log;

    // Масштаб последнего снимка: модель называет координаты в пикселях снимка, а не экрана.
    private double _scale = 1;
    private int _originX;
    private int _originY;
    private int _shotWidth;
    private int _shotHeight;

    public ComputerTools(ICommandApprover approver, string root, Action<string> log)
    {
        _approver = approver;
        _root = root;
        _log = log;
    }

    public static bool IsComputerTool(string tool) => tool.StartsWith("computer_", StringComparison.Ordinal);

    public async Task<ToolResult> RunAsync(string tool, ToolArgs args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult.Fail("Управление ПК пока только на Windows.");
        EnsureDpiAware();

        if (tool == "computer_screen")
            return ToolResult.Ok(Capture());

        if (FailsafeTriggered())
            throw new ToolException("Пользователь остановил управление ПК (курсор в левом верхнем углу экрана). Не продолжай действия на экране — опиши, что успел сделать, и спроси, продолжать ли.");

        var description = Describe(tool, args);
        if (!ClientConfig.LoadFullPcControl()
            && !await _approver.ApproveAsync("Управление ПК", description + "\n(чтобы не спрашивать каждый раз: /control on)", _root, ct))
            throw new ToolException("Пользователь не разрешил действие на экране. Не повторяй его; спроси, как поступить.");

        _log("🖱 " + description);
        switch (tool)
        {
            case "computer_click": Click(args); break;
            case "computer_move": MoveTo(Point(args)); break;
            case "computer_drag": Drag(args); break;
            case "computer_scroll": Scroll(args); break;
            case "computer_type": await TypeAsync(args, ct); break;
            case "computer_key": await KeysAsync(args.Required("keys", tool), ct); break;
            case "computer_open": await OpenAsync(args.Required("target", tool), ct); break;
            case "computer_wait": await Task.Delay(Math.Clamp(args.Int("ms") ?? 1000, 100, 15_000), ct); break;
            default: throw new ToolException($"Неизвестное действие {tool}.");
        }

        if (args.Bool("screenshot") == false)
            return ToolResult.Ok("Готово: " + description);
        await Task.Delay(Math.Clamp(args.Int("delay_ms") ?? 700, 100, 10_000), ct);
        return ToolResult.Ok(Capture("После: " + description + "."));
    }

    private static string Describe(string tool, ToolArgs args) => tool switch
    {
        "computer_click" => $"{(args.Str("button") ?? "left") switch { "right" => "Правый клик", "middle" => "Клик колесом", _ => args.Int("clicks") == 2 ? "Двойной клик" : "Клик" }} в ({args.Int("x")}, {args.Int("y")})",
        "computer_move" => $"Курсор в ({args.Int("x")}, {args.Int("y")})",
        "computer_drag" => $"Перетаскивание ({args.Int("x")}, {args.Int("y")}) → ({args.Int("to_x")}, {args.Int("to_y")})",
        "computer_scroll" => $"Прокрутка {(args.Int("amount") ?? -5) switch { < 0 => "вниз", _ => "вверх" }} на {Math.Abs(args.Int("amount") ?? -5)}",
        "computer_type" => $"Набор текста: «{Short(args.Str("text"))}»",
        "computer_key" => $"Клавиши: {args.Str("keys")}",
        "computer_open" => $"Открыть: {args.Str("target")}",
        "computer_wait" => $"Пауза {args.Int("ms") ?? 1000} мс",
        _ => tool,
    };

    private static string Short(string? text)
    {
        text ??= string.Empty;
        return text.Length <= 80 ? text : text[..80] + "…";
    }

    // ── Снимок экрана ───────────────────────────────────────────────────────

    private string Capture(string? prefix = null)
    {
        _originX = GetSystemMetrics(76);
        _originY = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0)
            throw new ToolException("Не удалось определить размер экрана.");

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        var pixels = new byte[width * height * 4];
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, _originX, _originY, 0x00CC0020 | 0x40000000))
                throw new ToolException("Не удалось снять экран (экран заблокирован?).");
            var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            SelectObject(memory, old);
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                throw new ToolException("Не удалось прочитать снимок экрана.");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }

        _scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        _shotWidth = Math.Max(1, (int)(width * _scale));
        _shotHeight = Math.Max(1, (int)(height * _scale));
        var rgb = new byte[_shotWidth * _shotHeight * 3];
        for (var y = 0; y < _shotHeight; y++)
        {
            var sy = Math.Min(height - 1, (int)(y / _scale));
            for (var x = 0; x < _shotWidth; x++)
            {
                var src = (sy * width + Math.Min(width - 1, (int)(x / _scale))) * 4;
                var dst = (y * _shotWidth + x) * 3;
                rgb[dst] = pixels[src + 2];
                rgb[dst + 1] = pixels[src + 1];
                rgb[dst + 2] = pixels[src];
            }
        }
        DrawCursor(rgb);

        var png = WindowTools.EncodePng(rgb, _shotWidth, _shotHeight);
        var note = $"{prefix} Экран {_shotWidth}×{_shotHeight} (координаты для computer_click — в пикселях ЭТОГО снимка; красный крестик — курсор).".Trim();
        return $"IMAGE_BASE64:image/png:{Convert.ToBase64String(png)}\n{note}";
    }

    /// <summary>Курсор на снимке — модель видит, куда наведена мышь.</summary>
    private void DrawCursor(byte[] rgb)
    {
        if (!GetCursorPos(out var p))
            return;
        var cx = (int)((p.X - _originX) * _scale);
        var cy = (int)((p.Y - _originY) * _scale);
        for (var d = -7; d <= 7; d++)
        {
            Plot(rgb, cx + d, cy);
            Plot(rgb, cx, cy + d);
        }
    }

    private void Plot(byte[] rgb, int x, int y)
    {
        if (x < 0 || y < 0 || x >= _shotWidth || y >= _shotHeight)
            return;
        var i = (y * _shotWidth + x) * 3;
        rgb[i] = 255;
        rgb[i + 1] = 0;
        rgb[i + 2] = 0;
    }

    // ── Мышь ────────────────────────────────────────────────────────────────

    private (int X, int Y) Point(ToolArgs args, string xName = "x", string yName = "y")
    {
        var x = args.Int(xName) ?? throw new ToolException($"Нужна координата {xName}.");
        var y = args.Int(yName) ?? throw new ToolException($"Нужна координата {yName}.");
        if (_shotWidth == 0)
            throw new ToolException("Сначала сделай снимок экрана (computer_screen) — координаты считаются по нему.");
        if (x < 0 || y < 0 || x >= _shotWidth || y >= _shotHeight)
            throw new ToolException($"Точка ({x}, {y}) вне снимка {_shotWidth}×{_shotHeight}.");
        return (_originX + (int)Math.Round(x / _scale), _originY + (int)Math.Round(y / _scale));
    }

    private static void MoveTo((int X, int Y) p)
    {
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = Math.Max(1, GetSystemMetrics(78) - 1);
        var vh = Math.Max(1, GetSystemMetrics(79) - 1);
        SendMouse(0x0001 | 0x8000 | 0x4000, (p.X - vx) * 65535 / vw, (p.Y - vy) * 65535 / vh);
    }

    private void Click(ToolArgs args)
    {
        if (args.Int("x") is not null)
            MoveTo(Point(args));
        Thread.Sleep(60);
        var (down, up) = (args.Str("button") ?? "left").ToLowerInvariant() switch
        {
            "right" => (0x0008u, 0x0010u),
            "middle" => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u),
        };
        var clicks = Math.Clamp(args.Int("clicks") ?? 1, 1, 3);
        for (var i = 0; i < clicks; i++)
        {
            SendMouse(down);
            SendMouse(up);
            Thread.Sleep(40);
        }
    }

    private void Drag(ToolArgs args)
    {
        var from = Point(args);
        var to = Point(args, "to_x", "to_y");
        MoveTo(from);
        Thread.Sleep(80);
        SendMouse(0x0002);
        for (var step = 1; step <= 12; step++)
        {
            MoveTo((from.X + (to.X - from.X) * step / 12, from.Y + (to.Y - from.Y) * step / 12));
            Thread.Sleep(20);
        }
        SendMouse(0x0004);
    }

    private void Scroll(ToolArgs args)
    {
        if (args.Int("x") is not null)
            MoveTo(Point(args));
        var amount = Math.Clamp(args.Int("amount") ?? -5, -50, 50);
        SendMouse(0x0800, 0, 0, (uint)(amount * 120));
    }

    // ── Клавиатура ──────────────────────────────────────────────────────────

    private static async Task TypeAsync(ToolArgs args, CancellationToken ct)
    {
        var text = args.Required("text", "computer_type");
        if (text.Length > MaxTextLength)
            throw new ToolException($"Слишком длинный текст (больше {MaxTextLength} символов) — раздели на части.");
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n')
            {
                SendVk(0x0D, false);
                SendVk(0x0D, true);
            }
            else
            {
                SendUnicode(ch, false);
                SendUnicode(ch, true);
            }
            await Task.Delay(8, ct);
        }
        if (args.Bool("enter") == true)
        {
            SendVk(0x0D, false);
            SendVk(0x0D, true);
        }
    }

    private static async Task KeysAsync(string keys, CancellationToken ct)
    {
        var combos = keys.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(VirtualKey).ToList())
            .ToList();
        if (combos.Count is 0 or > 20 || combos.Any(c => c.Count is 0 or > 4))
            throw new ToolException("Нужно 1–20 сочетаний, например \"CTRL+L\", \"WIN\", \"ALT+TAB\", \"ENTER\". Текст набирай через computer_type.");
        foreach (var combo in combos)
        {
            foreach (var key in combo)
                SendVk(key, false);
            await Task.Delay(50, ct);
            foreach (var key in Enumerable.Reverse(combo))
                SendVk(key, true);
            await Task.Delay(120, ct);
        }
    }

    private static ushort VirtualKey(string name)
    {
        var n = name.Trim().ToUpperInvariant();
        if (n.Length == 1 && n[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return n[0];
        if (n.Length is 2 or 3 && n[0] == 'F' && int.TryParse(n[1..], out var f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);
        return n switch
        {
            "ESC" or "ESCAPE" => 0x1B,
            "ENTER" or "RETURN" => 0x0D,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "BACKSPACE" => 0x08,
            "SHIFT" => 0x10,
            "CTRL" or "CONTROL" => 0x11,
            "ALT" => 0x12,
            "WIN" or "WINDOWS" or "META" or "CMD" => 0x5B,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "PRINTSCREEN" => 0x2C,
            "CAPSLOCK" => 0x14,
            "VOLUMEUP" => 0xAF,
            "VOLUMEDOWN" => 0xAE,
            "MUTE" => 0xAD,
            "PLAYPAUSE" => 0xB3,
            "-" or "MINUS" => 0xBD,
            "=" or "PLUS" => 0xBB,
            "." or "PERIOD" => 0xBE,
            "," or "COMMA" => 0xBC,
            _ => throw new ToolException($"Неизвестная клавиша «{name}». Можно: A–Z, 0–9, F1–F24, ESC, ENTER, SPACE, TAB, BACKSPACE, DELETE, SHIFT, CTRL, ALT, WIN, стрелки, HOME/END, PAGEUP/PAGEDOWN, VOLUMEUP/DOWN, MUTE."),
        };
    }

    // ── Открыть программу / сайт / файл ─────────────────────────────────────

    private static async Task OpenAsync(string target, CancellationToken ct)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Не путь и не адрес («telegram», «vpn») — ищем через меню «Пуск», как человек.
        }
        SendVk(0x5B, false);
        SendVk(0x5B, true);
        await Task.Delay(700, ct);
        foreach (var ch in target)
        {
            SendUnicode(ch, false);
            SendUnicode(ch, true);
            await Task.Delay(10, ct);
        }
        await Task.Delay(900, ct);
        SendVk(0x0D, false);
        SendVk(0x0D, true);
    }

    // ── WinAPI ──────────────────────────────────────────────────────────────

    private static bool _dpiAware;

    private static void EnsureDpiAware()
    {
        if (_dpiAware)
            return;
        // Без этого на экранах с масштабом 125–200% координаты мыши и снимка расходятся.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        _dpiAware = true;
    }

    private static bool FailsafeTriggered() => GetCursorPos(out var p)
        && p.X <= GetSystemMetrics(76) + 1 && p.Y <= GetSystemMetrics(77) + 1;

    private static void SendMouse(uint flags, int dx = 0, int dy = 0, uint data = 0)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendVk(ushort vk, bool up)
    {
        var extended = vk is >= 0x21 and <= 0x2E or 0x5B;
        var input = new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = (up ? 0x0002u : 0) | (extended ? 0x0001u : 0) },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicode(char ch, bool up)
    {
        var input = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = 0x0004u | (up ? 0x0002u : 0) } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
        public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

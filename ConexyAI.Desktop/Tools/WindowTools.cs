using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace ConexyAI.Desktop.Tools;

/// <summary>
/// Окна программ на ПК (Windows): список окон, снимок окна (игры, нативные приложения — то, что
/// page_screenshot не умеет) и нажатие клавиш в окне (например, R — перезагрузка шейдеров в Minecraft/Iris).
/// Снимок — с разрешением на программу до конца сессии; клавиши — с подтверждением на каждый вызов,
/// и никогда в терминалы/проводник (иначе нажатия могли бы стать командами).
/// </summary>
public sealed class WindowTools
{
    private const int MaxSide = 1600;
    private const int MaxKeyCombos = 12;

    // Сюда нажатия не отправляются ни при каком подтверждении: здесь клавиши = команды или управление системой.
    private static readonly HashSet<string> KeyBlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "wsl", "bash", "mintty",
        "explorer", "Taskmgr", "mmc", "regedit", "conexy", "mstsc", "SystemSettings", "ApplicationFrameHost",
    };

    private readonly ICommandApprover _approver;
    private readonly string _root;
    private readonly HashSet<string> _captureAllowed = new(StringComparer.OrdinalIgnoreCase);

    public WindowTools(ICommandApprover approver, string root)
    {
        _approver = approver;
        _root = root;
    }

    private sealed record WindowInfo(IntPtr Handle, string Title, string Process, int Pid, int Width, int Height, bool Minimized);

    // ── window_list ─────────────────────────────────────────────────────────

    public ToolResult List(ToolArgs args)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult.Fail("Работа с окнами пока только на Windows.");
        var filter = args.Str("filter");
        var windows = EnumerateWindows()
            .Where(w => filter is null || Matches(w, filter))
            .Take(60)
            .ToList();
        if (windows.Count == 0)
            return ToolResult.Ok(filter is null ? "Видимых окон нет." : $"Окон по «{filter}» нет — программа не запущена или ещё не открыла окно.");
        return ToolResult.Ok(string.Join('\n', windows.Select(w =>
            $"- «{w.Title}» — {w.Process}.exe (pid {w.Pid}), {w.Width}×{w.Height}{(w.Minimized ? ", свёрнуто" : string.Empty)}")));
    }

    // ── screen_capture ──────────────────────────────────────────────────────

    public async Task<ToolResult> CaptureAsync(ToolArgs args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult.Fail("Снимок окна пока только на Windows.");
        var query = args.Required("window", "screen_capture");
        var window = FindWindow(query);

        bool known;
        lock (_captureAllowed)
            known = _captureAllowed.Contains(window.Process);
        if (!known)
        {
            if (!await _approver.ApproveAsync("Снимок окна", $"«{window.Title}» ({window.Process}.exe)\n(разрешение действует на эту программу до конца сессии)", _root, ct))
                throw new ToolException("Пользователь не разрешил снимок окна. Не повторяй; попроси его прислать скриншот сам.");
            lock (_captureAllowed)
                _captureAllowed.Add(window.Process);
        }

        var previous = GetForegroundWindow();
        BringToFront(window.Handle);
        await Task.Delay(Math.Clamp(args.Int("delay_ms") ?? 600, 100, 10_000), ct);

        byte[] png;
        string note;
        try
        {
            (png, note) = CaptureClient(window.Handle);
        }
        finally
        {
            if (args.Bool("keep_focus") != true && previous != IntPtr.Zero && previous != window.Handle)
                BringToFront(previous);
        }
        return ToolResult.Ok($"IMAGE_BASE64:image/png:{Convert.ToBase64String(png)}\n{note}".TrimEnd());
    }

    // ── window_keys ─────────────────────────────────────────────────────────

    public async Task<ToolResult> KeysAsync(ToolArgs args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult.Fail("Нажатия клавиш пока только на Windows.");
        var window = FindWindow(args.Required("window", "window_keys"));
        if (KeyBlockedProcesses.Contains(window.Process))
            throw new ToolException($"Нажимать клавиши в {window.Process}.exe запрещено (терминал/системное окно).");

        var combos = ParseCombos(args.Required("keys", "window_keys"));
        if (!await _approver.ApproveAsync("Клавиши в окне", $"«{window.Title}» ({window.Process}.exe): {args.Str("keys")}", _root, ct))
            throw new ToolException("Пользователь не разрешил нажатия. Не повторяй; попроси его нажать сам.");

        var previous = GetForegroundWindow();
        BringToFront(window.Handle);
        await Task.Delay(300, ct);
        if (GetForegroundWindow() != window.Handle)
            throw new ToolException("Не удалось сделать окно активным — Windows не дала переключить фокус. Попроси пользователя кликнуть по окну и повтори.");

        foreach (var combo in combos)
        {
            foreach (var key in combo)
                SendKey(key, up: false);
            await Task.Delay(60, ct);
            foreach (var key in Enumerable.Reverse(combo))
                SendKey(key, up: true);
            await Task.Delay(Math.Clamp(args.Int("interval_ms") ?? 150, 30, 3000), ct);
        }

        if (args.Bool("keep_focus") == false && previous != IntPtr.Zero)
            BringToFront(previous);
        return ToolResult.Ok($"Нажато в «{window.Title}»: {args.Str("keys")}. Проверь результат через screen_capture.");
    }

    private static List<List<ushort>> ParseCombos(string keys)
    {
        var combos = new List<List<ushort>>();
        foreach (var token in keys.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var combo = token.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(VirtualKey).ToList();
            if (combo.Count is 0 or > 3)
                throw new ToolException($"Неверное сочетание «{token}». Пример: \"R\", \"F3+R\", \"ESC\", \"CTRL+S\".");
            combos.Add(combo);
        }
        if (combos.Count is 0 or > MaxKeyCombos)
            throw new ToolException($"Нужно от 1 до {MaxKeyCombos} сочетаний. Это инструмент для горячих клавиш, а не для набора текста.");
        return combos;
    }

    private static ushort VirtualKey(string name)
    {
        var n = name.Trim().ToUpperInvariant();
        if (n.Length == 1 && n[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return n[0];
        if (n.Length is 2 or 3 && n[0] == 'F' && int.TryParse(n[1..], out var f) && f is >= 1 and <= 12)
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
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "DELETE" or "DEL" => 0x2E,
            _ => throw new ToolException($"Неизвестная клавиша «{name}». Можно: A–Z, 0–9, F1–F12, ESC, ENTER, SPACE, TAB, SHIFT, CTRL, ALT, стрелки."),
        };
    }

    private static void SendKey(ushort vk, bool up)
    {
        // Скан-коды, а не виртуальные коды: игры (GLFW/LWJGL в Minecraft, DirectInput) читают именно их.
        var scan = (ushort)MapVirtualKey(vk, 0);
        var extended = vk is >= 0x21 and <= 0x2E;
        var input = new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = scan,
                dwFlags = 0x0008 | (up ? 0x0002u : 0) | (extended ? 0x0001u : 0),
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    // ── Поиск окон ──────────────────────────────────────────────────────────

    private static bool Matches(WindowInfo w, string query) =>
        w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || w.Process.Equals(query, StringComparison.OrdinalIgnoreCase)
        || w.Process.Equals(Path.GetFileNameWithoutExtension(query), StringComparison.OrdinalIgnoreCase);

    private static WindowInfo FindWindow(string query)
    {
        var all = EnumerateWindows().ToList();
        var found = all.Where(w => Matches(w, query)).ToList();
        if (found.Count == 0)
            throw new ToolException($"Окно «{query}» не найдено. Открытые окна: window_list. Возможно, программа ещё запускается — подожди и повтори.");
        // Несколько совпадений (лаунчер + игра) — берём самое большое окно.
        return found.OrderByDescending(w => (long)w.Width * w.Height).First();
    }

    private static IEnumerable<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();
        var self = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
                return true;
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self)
                return true;
            string process;
            try { process = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { return true; }
            GetWindowRect(hwnd, out var r);
            var minimized = IsIconic(hwnd);
            if (!minimized && (r.Right - r.Left < 80 || r.Bottom - r.Top < 60))
                return true;
            result.Add(new WindowInfo(hwnd, sb.ToString(), process, (int)pid, r.Right - r.Left, r.Bottom - r.Top, minimized));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9); // SW_RESTORE
        // Windows не даёт фоновому процессу забрать фокус; нажатие Alt снимает этот запрет.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 0x0002, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    // ── Снимок ──────────────────────────────────────────────────────────────

    private static bool _dpiAware;

    private static (byte[] Png, string Note) CaptureClient(IntPtr hwnd)
    {
        if (!_dpiAware)
        {
            // Без этого на экранах с масштабом 125–200% координаты окна «уменьшены», и снимок обрезается.
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            _dpiAware = true;
        }

        GetClientRect(hwnd, out var client);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
            throw new ToolException("У окна нет видимой области (свёрнуто или ещё не отрисовано).");

        // Копируем с экрана, а не PrintWindow: OpenGL/DirectX-игры через PrintWindow дают чёрный кадр.
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        var pixels = new byte[width * height * 4];
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000))
                throw new ToolException("Не удалось скопировать изображение окна.");
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // сверху вниз
                biPlanes = 1,
                biBitCount = 32,
            };
            SelectObject(memory, old);
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                throw new ToolException("Не удалось прочитать изображение окна.");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }

        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        var outW = Math.Max(1, (int)(width * scale));
        var outH = Math.Max(1, (int)(height * scale));
        var rgb = new byte[outW * outH * 3];
        long brightness = 0;
        for (var y = 0; y < outH; y++)
        {
            var sy = (int)(y / scale);
            for (var x = 0; x < outW; x++)
            {
                var src = (sy * width + (int)(x / scale)) * 4;
                var dst = (y * outW + x) * 3;
                rgb[dst] = pixels[src + 2];
                rgb[dst + 1] = pixels[src + 1];
                rgb[dst + 2] = pixels[src];
                brightness += rgb[dst] + rgb[dst + 1] + rgb[dst + 2];
            }
        }

        var average = brightness / (3.0 * outW * outH);
        var note = $"Окно {width}×{height}{(scale < 1 ? $", снимок уменьшен до {outW}×{outH}" : string.Empty)}, средняя яркость {average:0}/255.";
        if (average < 6)
            note += " Кадр почти полностью чёрный: либо так рендерит программа (для шейдера — смотри лог ошибок компиляции), либо окно перекрыто/свёрнуто.";
        return (EncodePng(rgb, outW, outH), note);
    }

    private static byte[] EncodePng(byte[] rgb, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0); // фильтр None
                z.Write(rgb, y * width * 3, width * 3);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8; // бит на канал
        header[9] = 2; // RGB
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        s.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, (int)Crc32(typeBytes, data));
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 чанка PNG: считается по типу и данным.</summary>
    internal static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in type)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    // ── WinAPI ──────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
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
        [FieldOffset(8)] public MOUSEINPUT mi; // задаёт размер union, как в Windows SDK
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
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

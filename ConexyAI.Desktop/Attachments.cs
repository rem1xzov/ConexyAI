using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop;

/// <summary>Файл, который уйдёт агенту вместе со следующим сообщением (как вложение на сайте).</summary>
public sealed record PendingAttachment(string FileName, string ContentType, byte[] Bytes, bool IsImage);

/// <summary>
/// Вложения для терминала: перетащенный в окно файл (путь), «/attach путь», «@путь» в тексте и
/// вставка из буфера обмена (картинка или файлы из проводника). Сервер читает их так же, как веб:
/// картинки уходят модели, документы разбираются в текст.
/// </summary>
public static partial class Attachments
{
    public const int MaxFiles = 10;
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private const long MaxTotalBytes = 30L * 1024 * 1024; // запрос на сервере ограничен ~55 МБ вместе с base64

    public static List<PendingAttachment> Pending { get; } = [];

    private static int _clipCounter;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".json"] = "application/json", [".csv"] = "text/csv", [".html"] = "text/html", [".xml"] = "application/xml",
    };

    public static long TotalBytes => Pending.Sum(a => (long)a.Bytes.Length);

    /// <summary>Добавляет файл с диска. Возвращает null при успехе или текст ошибки.</summary>
    public static string? AddFile(string path)
    {
        path = path.Trim().Trim('"');
        if (path.StartsWith("~"))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('\\', '/'));
        if (!File.Exists(path))
            return $"Файл не найден: {path}";
        if (Pending.Count >= MaxFiles)
            return $"Можно прикрепить не больше {MaxFiles} файлов.";

        var info = new FileInfo(path);
        // Тот же файл мог прийти дважды (наш Ctrl+V и вставка пути самим терминалом) — не дублируем.
        if (Pending.Any(p => p.FileName == info.Name && p.Bytes.Length == info.Length))
            return null;
        if (info.Length > MaxFileBytes)
            return $"Файл «{info.Name}» больше {MaxFileBytes / 1024 / 1024} МБ.";
        if (TotalBytes + info.Length > MaxTotalBytes)
            return "Суммарный размер вложений больше 30 МБ.";

        var ext = info.Extension;
        var type = Types.TryGetValue(ext, out var t) ? t : "text/plain";
        Pending.Add(new PendingAttachment(info.Name, type, File.ReadAllBytes(path), type.StartsWith("image/")));
        return null;
    }

    public static void Clear() => Pending.Clear();

    public static bool Remove(int number)
    {
        if (number < 1 || number > Pending.Count)
            return false;
        Pending.RemoveAt(number - 1);
        return true;
    }

    /// <summary>Убирает последнее вложение (Ctrl+Z в строке ввода) и возвращает его имя.</summary>
    public static string? RemoveLast()
    {
        if (Pending.Count == 0)
            return null;
        var last = Pending[^1];
        Pending.RemoveAt(Pending.Count - 1);
        return last.FileName;
    }

    /// <summary>Строка-«чипсы» над полем ввода.</summary>
    public static string Describe()
    {
        if (Pending.Count == 0)
            return string.Empty;
        var parts = Pending.Select((a, i) =>
            $"[{i + 1}] {(a.IsImage ? "img" : "file")} {(a.FileName.Length > 28 ? a.FileName[..27] + "…" : a.FileName)} ({Size(a.Bytes.Length)})");
        return string.Join("  ", parts);
    }

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} МБ" : $"{Math.Max(1, bytes / 1024)} КБ";

    // ── Пути в тексте сообщения ─────────────────────────────────────────────

    /// <summary>
    /// Вытаскивает из строки пути существующих файлов: «"C:\a b.png"» (так вставляет перетаскивание),
    /// «@путь» и строку, целиком состоящую из пути. Найденное прикрепляется и убирается из текста.
    /// </summary>
    public static string ExtractFiles(string line, string root, List<string> errors)
    {
        var text = line;

        string Resolve(string p)
        {
            p = p.Trim().Trim('"');
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(root, p));
        }

        // Относительные имена в кавычках — обычный текст задачи («открой "app.js"»), а не вложение:
        // автоматически прикрепляем только абсолютные пути (так вставляет перетаскивание) или помеченные @.
        bool TryAttach(string raw, bool anyPath = false)
        {
            if (!anyPath && !Path.IsPathRooted(raw.Trim().Trim('"')))
                return false;
            var full = Resolve(raw);
            if (!File.Exists(full))
                return false;
            if (AddFile(full) is { } err)
                errors.Add(err);
            return true;
        }

        text = QuotedPath().Replace(text, m => TryAttach(m.Groups[1].Value) ? string.Empty : m.Value);
        text = AtPath().Replace(text, m => TryAttach(m.Groups[1].Value.Trim('"'), anyPath: true) ? string.Empty : m.Value);
        // Терминал вставляет несколько скопированных файлов через пробел без кавычек.
        text = BarePath().Replace(text, m => TryAttach(m.Groups[1].Value) ? string.Empty : m.Value);

        var whole = text.Trim();
        if (whole.Length > 0 && whole.IndexOfAny(Path.GetInvalidPathChars()) < 0 && TryAttach(whole))
            text = string.Empty;

        return text.Trim();
    }

    // ── Буфер обмена (Windows) ──────────────────────────────────────────────

    /// <summary>
    /// Что было в буфере: картинка или файлы прикрепляются, текст возвращается для вставки в строку.
    /// Возвращает (текст для вставки, сообщение для пользователя).
    /// </summary>
    public static (string? Text, string? Message) PasteFromClipboard()
    {
        if (!OperatingSystem.IsWindows())
            return (null, "Вставка из буфера обмена поддерживается только в Windows.");

        const string script = """
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            if ([Windows.Forms.Clipboard]::ContainsFileDropList()) {
                foreach ($f in [Windows.Forms.Clipboard]::GetFileDropList()) { 'FILE:' + $f }
            } elseif ([Windows.Forms.Clipboard]::ContainsImage()) {
                $img = [Windows.Forms.Clipboard]::GetImage()
                $p = Join-Path $env:TEMP ('conexy-clip-' + [guid]::NewGuid().ToString('N') + '.png')
                $img.Save($p, [Drawing.Imaging.ImageFormat]::Png)
                'IMAGE:' + $p
            } elseif ([Windows.Forms.Clipboard]::ContainsText()) {
                'TEXT:' + [Windows.Forms.Clipboard]::GetText()
            }
            """;

        string output;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Sta");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(psi)!;
            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                process.Kill(true);
                return (null, "Буфер обмена не ответил.");
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, "Не удалось прочитать буфер обмена: " + ex.Message);
        }

        var added = new List<string>();
        string? errors = null;
        string? text = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("FILE:"))
            {
                if (AddFile(raw[5..]) is { } err)
                    errors = err;
                else
                    added.Add(Path.GetFileName(raw[5..]));
            }
            else if (raw.StartsWith("IMAGE:"))
            {
                var temp = raw[6..].Trim();
                try
                {
                    var name = $"clipboard-{++_clipCounter}.png";
                    var bytes = File.ReadAllBytes(temp);
                    if (Pending.Count >= MaxFiles || TotalBytes + bytes.Length > MaxTotalBytes)
                        errors = "Вложений слишком много или они слишком большие.";
                    else
                    {
                        Pending.Add(new PendingAttachment(name, "image/png", bytes, true));
                        added.Add(name);
                    }
                }
                finally
                {
                    try { File.Delete(temp); } catch (IOException) { }
                }
            }
            else if (raw.StartsWith("TEXT:"))
            {
                text = raw[5..] + "\n" + string.Join('\n', output.Replace("\r\n", "\n").Split('\n').SkipWhile(l => !l.StartsWith("TEXT:")).Skip(1));
                break;
            }
        }

        if (text is not null)
            return (text.Replace("\r", string.Empty).Replace('\n', ' ').TrimEnd(), null);
        if (added.Count > 0)
            return (null, "Прикреплено: " + string.Join(", ", added));
        return (null, errors ?? "В буфере обмена нет картинки, файлов или текста.");
    }

    // ── Автоматическое прикрепление из буфера ───────────────────────────────
    // Windows Terminal перехватывает Ctrl+V сам: текст он вставляет как клавиши, а картинку или
    // файлы из Проводника не присылает вообще, поэтому обработчик Ctrl+V в приложении до них
    // не доходит. Пока пользователь в строке ввода, следим за номером изменения буфера (дешёвый
    // вызов без чтения содержимого) и приклеиваем картинки/файлы сами.

    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfHDrop = 15;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    private static uint _clipboardSequence;

    /// <summary>Запоминает текущее состояние буфера: уже скопированное до ввода не приклеиваем.</summary>
    public static void WatchClipboardFromNow()
    {
        if (OperatingSystem.IsWindows())
            _clipboardSequence = GetClipboardSequenceNumber();
    }

    /// <summary>
    /// Проверяет, не скопировали ли картинку или файлы, и прикрепляет их. Текст не трогаем —
    /// его вставляет сам терминал по Ctrl+V.
    /// </summary>
    public static bool PollClipboard(out string message)
    {
        message = string.Empty;
        if (!OperatingSystem.IsWindows())
            return false;

        var sequence = GetClipboardSequenceNumber();
        if (sequence == _clipboardSequence)
            return false;
        _clipboardSequence = sequence;

        // Только картинка или список файлов: обычный текст — не наше дело.
        if (!IsClipboardFormatAvailable(CfDib) && !IsClipboardFormatAvailable(CfBitmap) &&
            !IsClipboardFormatAvailable(CfHDrop))
            return false;

        var before = Pending.Count;
        var (text, note) = PasteFromClipboard();
        if (text is not null || Pending.Count == before)
            return false;

        message = note ?? "Прикреплено: " + string.Join(", ", Pending.Skip(before).Select(a => a.FileName));
        return true;
    }

    [GeneratedRegex("\"([^\"]+\\.[A-Za-z0-9]{1,8})\"")]
    private static partial Regex QuotedPath();

    [GeneratedRegex("(?:^|\\s)@(\"[^\"]+\"|\\S+)")]
    private static partial Regex AtPath();

    [GeneratedRegex("(?<![\\w\"])([A-Za-z]:\\\\[^\\s\"]+\\.[A-Za-z0-9]{1,8})")]
    private static partial Regex BarePath();
}

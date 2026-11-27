namespace ConexyAI.Contract;

// ATTACHMENT_MEDIA: единая классификация вложений пользователя — что из них изображение, какое
// изображение модель способна принять и что является документом.
//
// Почему так: провайдер определяет формат картинки по СОДЕРЖИМОМУ байтов, а не по имени файла или
// объявленному MIME (DeepSeek vision docs). Браузеры и устройства при этом присылают MIME неточно:
// на Android файл из галереи/проводника нередко приходит как application/octet-stream или вообще без
// типа. Раньше такое фото считалось «документом», и модель его не видела вовсе. Поэтому тип берём из
// сигнатуры байтов, а объявленный MIME/расширение — лишь подсказка для нечитаемых сигнатур.
public static class AttachmentMedia
{
    /// <summary>Форматы изображений, которые принимает модель, по содержимому.</summary>
    public const string SupportedImageFormats = "JPEG, PNG, GIF, WebP";

    /// <summary>Сколько base64-символов нужно декодировать для сигнатуры (44 = 33 байта).</summary>
    private const int SniffBase64Chars = 44;

    private static readonly Dictionary<string, string> ModelImageMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "image/jpeg",
        ["image/jpg"] = "image/jpeg",
        ["image/pjpeg"] = "image/jpeg",
        ["image/png"] = "image/png",
        ["image/gif"] = "image/gif",
        ["image/webp"] = "image/webp",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".heic", ".heif", ".avif", ".tif", ".tiff", ".ico",
    };

    /// <summary>Канонический MIME: синонимы (image/jpg, image/pjpeg) приводятся к image/jpeg.</summary>
    public static string NormalizeMime(string? mime)
    {
        var value = (mime ?? string.Empty).Trim().ToLowerInvariant();
        return ModelImageMimes.TryGetValue(value, out var canonical) ? canonical : value;
    }

    /// <summary>MIME, который модель принимает как изображение (по содержимому).</summary>
    public static bool IsModelImageMime(string? mime) =>
        ModelImageMimes.ContainsKey((mime ?? string.Empty).Trim());

    /// <summary>Content type по сигнатуре байтов; <c>null</c> — это не распознанное изображение.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes)) return "image/png";
        if (IsJpeg(bytes)) return "image/jpeg";
        if (IsGif(bytes)) return "image/gif";
        if (IsBmp(bytes)) return "image/bmp";
        if (IsWebp(bytes)) return "image/webp";
        return null;
    }

    /// <summary>
    /// Тип, с которым вложение можно отправить модели (по содержимому), или <c>null</c>.
    /// <para>
    /// Если сигнатура не распознана (например, редкий кодировщик), но объявленный MIME — поддерживаемый
    /// моделью формат, доверяем объявлению: провайдер всё равно решит по содержимому.
    /// </para>
    /// </summary>
    public static string? ModelImageType(TaskAttachment attachment)
    {
        if (!IsImage(attachment)) return null;

        var payload = attachment.ContentBase64?.Trim();
        var detected = DetectContentTypeFromBase64(payload);
        if (detected is not null)
            return IsModelImageMime(detected) ? NormalizeMime(detected) : null;

        var declared = NormalizeMime(attachment.ContentType);
        return IsModelImageMime(declared) ? declared : null;
    }

    /// <summary>Это изображение вообще (включая форматы, которые модель не принимает).</summary>
    public static bool IsImage(TaskAttachment attachment)
    {
        var declared = NormalizeMime(attachment.ContentType);
        if (declared.StartsWith("image/", StringComparison.Ordinal)) return true;
        if (ImageExtensions.Contains(Path.GetExtension(attachment.FileName ?? string.Empty))) return true;

        // Провайдер/браузер не сообщили тип — решает сигнатура. Так фото, пришедшее как
        // application/octet-stream (частый случай на Android), снова становится картинкой.
        return IsGenericMime(declared) && DetectContentTypeFromBase64(attachment.ContentBase64?.Trim()) is not null;
    }

    /// <summary>Объявленный MIME не несёт информации о типе файла.</summary>
    public static bool IsGenericMime(string? mime) =>
        string.IsNullOrEmpty(mime) || string.Equals(mime, "application/octet-stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>Content type по первым байтам base64; <c>null</c> — сигнатура не распознана.</summary>
    public static string? DetectContentTypeFromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        var payload = base64.Trim();
        var take = Math.Min(payload.Length, SniffBase64Chars);
        take -= take % 4;
        if (take < 4) return null;

        try
        {
            var bytes = Convert.FromBase64String(payload[..take]);
            return DetectContentType(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Оценка размера изображения до декодирования base64 (погрешность только в сторону большего).</summary>
    public static long ApproximateImageBytes(string? base64) => (base64?.Length ?? 0) * 3L / 4;

    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsGif(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8';

    private static bool IsBmp(ReadOnlySpan<byte> b) => b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M';

    private static bool IsWebp(ReadOnlySpan<byte> b) =>
        b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
        && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
}

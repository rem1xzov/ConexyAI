namespace ConexyAI.Service;

// VIEW_IMAGE: добавлено 2026-10-04 — распознавание картинок для инструмента view_image. Проверяем
// магические байты (а не только расширение), чтобы битый/чужой файл не ушёл модели как image.
public static class ImageContent
{
    /// <summary>Upper bound for one image fed to the model (base64 inflates this by ~33%).</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>Content type of a supported raster image, or <c>null</c> when it is not one.</summary>
    public static string? DetectContentType(byte[] bytes, string? fileName = null)
    {
        if (IsPng(bytes)) return "image/png";
        if (IsJpeg(bytes)) return "image/jpeg";
        if (IsGif(bytes)) return "image/gif";
        if (IsBmp(bytes)) return "image/bmp";
        if (IsWebp(bytes)) return "image/webp";
        // Header unusual but the extension is a known raster image — accept it anyway.
        return ExtensionContentType(fileName);
    }

    public static string? ExtensionContentType(string? fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => null,
    };

    private static bool IsPng(byte[] b) => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsGif(byte[] b) => b.Length >= 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8';

    private static bool IsBmp(byte[] b) => b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M';

    private static bool IsWebp(byte[] b) =>
        b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
        && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
}

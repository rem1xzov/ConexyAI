using ConexyAI.Contract;

namespace ConexyAI.Service;

// VIEW_IMAGE: добавлено 2026-10-04 — распознавание картинок для инструмента view_image. Проверяем
// магические байты (а не только расширение), чтобы битый/чужой файл не ушёл модели как image.
// ATTACHMENT_MEDIA: детектор форматов вынесен в общий AttachmentMedia — один источник правды для
// вложений чата, инструмента view_image и вложений устройств.
public static class ImageContent
{
    /// <summary>Upper bound for one image fed to the model by a tool (base64 inflates this by ~33%).</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>Content type of a supported raster image, or <c>null</c> when it is not one.</summary>
    public static string? DetectContentType(byte[] bytes, string? fileName = null) =>
        AttachmentMedia.DetectContentType(bytes) ?? ExtensionContentType(fileName);

    public static string? ExtensionContentType(string? fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => null,
    };
}

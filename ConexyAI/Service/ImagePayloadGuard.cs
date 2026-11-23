using System.Text;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// IMAGE_PAYLOAD_GUARD: модель принимает только png/jpeg/webp. Битой картинке, bmp/gif или картинке
// с несовпадающим MIME нельзя ронять весь ход через 400 от провайдера: такой блок вырезается до
// отправки, а текст задачи продолжает выполняться. Это не молчаливая потеря — факт пишется в лог.
public static class ImagePayloadGuard
{
    private const int MaxBytes = 8 * 1024 * 1024;

    public static bool IsModelImageMime(string? mime)
    {
        var normalized = (mime ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "image/png" or "image/jpeg" or "image/jpg" or "image/webp";
    }

    /// <summary>Нормализует data-url или возвращает null, если картинку нельзя отправлять модели.</summary>
    public static string? NormalizeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
            return null;
        var raw = dataUrl.Trim();
        if (!raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var comma = raw.IndexOf(',');
        if (comma < 0)
            return null;
        var header = raw[5..comma];
        var payload = raw[(comma + 1)..];
        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mime = parts.FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        if (!parts.Any(p => p.Equals("base64", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!IsModelImageMime(mime))
            return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException) { return null; }
        if (bytes.Length == 0 || bytes.LongLength > MaxBytes)
            return null;
        if (!IsValidImage(bytes, mime))
            return null;

        var normalizedMime = mime == "image/jpg" ? "image/jpeg" : mime;
        return $"data:{normalizedMime};base64,{Convert.ToBase64String(bytes)}";
    }

    public static bool IsValidImage(byte[] bytes, string? declaredMime)
    {
        var declared = (declaredMime ?? string.Empty).ToLowerInvariant();
        if (declared == "image/jpg") declared = "image/jpeg";
        if (!IsModelImageMime(declared))
            return false;
        var detected = ImageContent.DetectContentType(bytes);
        if (detected is null)
            return false;
        if (!string.Equals(detected, declared, StringComparison.OrdinalIgnoreCase))
            return false;
        return detected switch
        {
            "image/png" => HasPngEnd(bytes),
            "image/jpeg" => HasJpegEnd(bytes),
            "image/webp" => bytes.Length >= 16,
            _ => false,
        };
    }

    /// <summary>Копия сообщений без невалидных image_url-блоков.</summary>
    public static (List<ChatMessage> Messages, int Removed) Sanitize(IReadOnlyList<ChatMessage> messages)
    {
        var removed = 0;
        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Content is not IEnumerable<ContentBlock> rawBlocks)
            {
                result.Add(message);
                continue;
            }

            var blocks = rawBlocks.ToList();
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var block = blocks[i];
                if (!string.Equals(block.Type, "image_url", StringComparison.OrdinalIgnoreCase))
                    continue;
                var normalized = NormalizeDataUrl(block.ImageUrl?.Url);
                if (normalized is null)
                {
                    blocks.RemoveAt(i);
                    removed++;
                    continue;
                }
                blocks[i] = block with { ImageUrl = new ImageUrlContent(normalized) };
            }

            if (blocks.Count == 0)
            {
                result.Add(message with { Content = string.Empty });
                continue;
            }
            result.Add(message with { Content = blocks });
        }
        return (result, removed);
    }

    private static bool HasPngEnd(byte[] bytes)
    {
        var needle = Encoding.ASCII.GetBytes("IEND");
        var from = Math.Max(0, bytes.Length - 64);
        for (var i = bytes.Length - needle.Length; i >= from; i--)
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    private static bool HasJpegEnd(byte[] bytes) =>
        bytes.Length >= 4 && bytes[^2] == 0xFF && bytes[^1] == 0xD9;
}

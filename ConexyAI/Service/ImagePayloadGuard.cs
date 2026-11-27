using ConexyAI.Contract;

namespace ConexyAI.Service;

// IMAGE_PAYLOAD_GUARD: модель принимает изображения JPEG, PNG, GIF и WebP, определяя формат по
// СОДЕРЖИМОМУ байтов (см. DeepSeek vision docs), а не по объявленному MIME. Наружу уходит нормализованный
// data URL с типом, выведенным из сигнатуры: так фото, которое браузер/проводник отдал с неточным или
// пустым MIME, доезжает до модели, а не вырезается молча.
//
// Задача гарда — не «проверить картинку целиком», а не дать провайдеру отклонить весь запрос из-за
// неподдерживаемого формата или слишком большого вложения. Поэтому здесь всё по контракту провайдера:
// формат по сигнатуре, лимит 32 МиБ на одно inline-изображение.
public static class ImagePayloadGuard
{
    /// <summary>Лимит одного inline-изображения в запросе к модели (DeepSeek vision: 32 МиБ).</summary>
    public const long MaxInlineImageBytes = 32L * 1024 * 1024;

    public static bool IsModelImageMime(string? mime) => AttachmentMedia.IsModelImageMime(mime);

    /// <summary>
    /// Нормализует data-url: тип берётся из содержимого, неподдерживаемый формат или превышение
    /// лимита — <c>null</c>.
    /// </summary>
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
        if (!parts.Any(p => p.Equals("base64", StringComparison.OrdinalIgnoreCase)))
            return null;

        if (AttachmentMedia.ApproximateImageBytes(payload) is <= 0 or > MaxInlineImageBytes)
            return null;

        var detected = AttachmentMedia.DetectContentTypeFromBase64(payload);
        if (detected is null || !AttachmentMedia.IsModelImageMime(detected))
            return null;

        // Провайдер судит по содержимому, поэтому MIME в data-url — производное от сигнатуры, а не от
        // того, что прислал клиент. Переводы строк в base64 внутри JSON не нужны и только вредят.
        var mime = AttachmentMedia.NormalizeMime(detected);
        var normalizedPayload = payload.IndexOfAny(['\r', '\n', ' ', '\t']) < 0
            ? payload
            : new string(payload.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return $"data:{mime};base64,{normalizedPayload}";
    }

    /// <summary>
    /// Являются ли байты изображением поддерживаемого моделью формата. Формат определяется по
    /// содержимому; объявленный MIME не решает (провайдер тоже его игнорирует).
    /// </summary>
    public static bool IsValidImage(byte[] bytes, string? declaredMime)
    {
        _ = declaredMime; // параметр сохранён в сигнатуре ради читаемости вызовов: тип не решает.
        var detected = AttachmentMedia.DetectContentType(bytes);
        return detected is not null && AttachmentMedia.IsModelImageMime(detected);
    }

    /// <summary>Копия сообщений с нормализованными image_url-блоками; неподдерживаемые удаляются.</summary>
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
}

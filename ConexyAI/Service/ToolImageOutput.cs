using System.Buffers.Text;

namespace ConexyAI.Service;

// PHONE_AGENT: добавлено 2026-10-29 — общий разбор ответа инструмента с картинкой
// `IMAGE_BASE64:<mime>:<base64>[\nзаметка клиента]` (скриншот ПК, скриншот и фото с телефона).
public static class ToolImageOutput
{
    public const string Prefix = "IMAGE_BASE64:";

    private static readonly HashSet<string> AllowedMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif", "image/bmp",
    };

    public static bool IsImage(string? output) =>
        output is not null && output.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Делит ответ на mime, base64 и заметку (без проверок).</summary>
    public static (string Mime, string Base64, string Note) Split(string output)
    {
        var payload = output.StartsWith(Prefix, StringComparison.Ordinal) ? output[Prefix.Length..] : output;
        var noteStart = payload.IndexOf('\n');
        var note = noteStart >= 0 ? payload[(noteStart + 1)..].Trim() : string.Empty;
        if (noteStart >= 0)
            payload = payload[..noteStart];
        var colon = payload.IndexOf(':');
        var mime = colon > 0 ? payload[..colon] : "image/png";
        var base64 = colon > 0 ? payload[(colon + 1)..] : payload;
        return (mime, base64, note);
    }

    /// <summary>
    /// Разбор с проверкой для данных, пришедших с устройства: известный image-mime, валидный base64,
    /// размер не больше <see cref="ImageContent.MaxBytes"/>. Ошибка — текст для модели.
    /// </summary>
    public static bool TryParse(string output, out string mime, out string base64, out string note, out string? error)
    {
        (mime, base64, note) = Split(output);
        error = null;
        mime = mime.Trim().ToLowerInvariant();
        if (!AllowedMimes.Contains(mime))
        {
            error = "Телефон прислал неподдерживаемый тип изображения.";
            return false;
        }
        base64 = base64.Trim();
        if (base64.Length == 0 || !Base64.IsValid(base64))
        {
            error = "Телефон прислал повреждённое изображение.";
            return false;
        }
        if ((long)base64.Length * 3 / 4 > ImageContent.MaxBytes)
        {
            error = $"Изображение слишком большое (лимит {ImageContent.MaxBytes / (1024 * 1024)} МБ). Выбери другое или уменьши размер.";
            return false;
        }
        return true;
    }
}

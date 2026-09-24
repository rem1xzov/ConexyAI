using ConexyAI.Contract;

namespace ConexyAI.Service;

// OFFICE_FORMATS: добавлено 2026-09-23
/// <summary>
/// Puts the text of document attachments into the user's message, for every mode.
/// <para>
/// Before this, <see cref="ChatMessageFactory.User"/> forwarded images only: a .docx/.xlsx/.pptx/.pdf
/// attached in flash, pro or students never reached the model at all, and the agents only got the
/// raw bytes in their workspace, unreadable for .docx/.xlsx/.pptx. The composed text is also what the
/// history stores, so a follow-up question about the document still has it.
/// </para>
/// </summary>
public static class AttachmentText
{
    // A single document may fill most of a turn, but several must not push the context window: the
    // history keeps up to 20 rows and every one of them is re-sent with each request.
    public const int MaxCharsPerAttachment = 30_000;
    public const int MaxCharsPerMessage = 60_000;

    public static string ComposeUserMessage(string prompt, IReadOnlyList<TaskAttachment>? attachments)
    {
        // TEXT_DECODING: добавлено 2026-09-24 (ревью M6) — сообщение сохраняется в историю (Postgres):
        // NUL или одиночный суррогат в нём роняли вставку, и терялся весь ход.
        prompt = TextSanitizer.Clean(prompt);
        var documents = attachments?
            .Where(a => !a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (documents is null || documents.Count == 0)
        {
            return prompt;
        }

        var sb = new System.Text.StringBuilder(prompt);
        var budget = MaxCharsPerMessage;
        foreach (var attachment in documents)
        {
            var name = TextSanitizer.Truncate(TextSanitizer.Clean(Path.GetFileName(attachment.FileName ?? string.Empty)), 200);
            if (string.IsNullOrWhiteSpace(name)) name = "вложение";

            sb.Append("\n\n");
            var text = Extract(attachment, name);
            if (text is null)
            {
                sb.Append($"[Вложение «{name}»: текст из этого формата не извлекается. Поддерживаются .docx, .xlsx, .pptx, .pdf и текстовые файлы; " +
                          "старые .doc/.xls/.ppt нужно сохранить в новом формате.]");
                continue;
            }

            if (text.Length == 0)
            {
                sb.Append($"[Вложение «{name}»: текста в файле не найдено — возможно, это скан или пустой документ.]");
                continue;
            }

            var limit = Math.Min(MaxCharsPerAttachment, budget);
            if (limit <= 0)
            {
                sb.Append($"[Вложение «{name}» не включено: превышен объём вложений для одного сообщения.]");
                continue;
            }

            var truncated = text.Length > limit;
            // Never between the halves of a surrogate pair (an emoji at the cut would leave a lone one).
            var body = truncated ? TextSanitizer.Truncate(text, limit) : text;
            budget -= body.Length;

            sb.Append($"--- Вложение «{name}» ---\n{body}\n");
            sb.Append(truncated
                ? $"--- Конец вложения «{name}» (показаны первые {limit} из {text.Length} символов) ---"
                : $"--- Конец вложения «{name}» ---");
        }

        return sb.ToString();
    }

    /// <summary>The attachment's text; <c>null</c> when the format is not readable as text.</summary>
    private static string? Extract(TaskAttachment attachment, string fileName)
    {
        if (!DocumentParser.CanExtract(fileName))
        {
            return null;
        }

        try
        {
            // Parse decodes text by its BOM/encoding and strips NUL and other control characters.
            var bytes = Convert.FromBase64String(attachment.ContentBase64?.Trim() ?? string.Empty);
            return DocumentParser.Parse(bytes, fileName).Trim();
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}

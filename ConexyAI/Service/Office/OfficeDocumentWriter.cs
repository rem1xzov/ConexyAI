using System.Text;
using System.Xml;

namespace ConexyAI.Service.Office;

// OFFICE_FORMATS: добавлено 2026-09-23
public enum OfficeFormat
{
    Docx,
    Xlsx,
    Pptx,
}

/// <summary>
/// Builds .docx / .xlsx / .pptx files from Markdown. Used by the agents' <c>create_document</c> tool
/// and by the "download as…" export available for every model's answer.
/// </summary>
/// <remarks>
/// OFFICE_OPENXML: переписано 2026-09-24 — раньше пакеты склеивались из XML-строк вручную: без стилей
/// списков, без свойств документа, с риском невалидной структуры (ТЗ этап 3.1, ТЗ-2 §7). Теперь
/// .docx и .pptx строятся через DocumentFormat.OpenXml, .xlsx — через ClosedXML; каждый результат
/// проходит OpenXmlValidator без ошибок (см. DocumentTests). Публичный API не менялся.
/// </remarks>
public static partial class OfficeDocumentWriter
{
    public const int MaxMarkdownChars = 2_000_000;

    /// <summary>Written into the core properties of every generated file.</summary>
    internal const string Creator = "ConexyAI";

    public static bool TryParseFormat(string? value, out OfficeFormat format)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Contains('.'))
        {
            normalized = Path.GetExtension(normalized).TrimStart('.');
        }

        switch (normalized)
        {
            case "docx": format = OfficeFormat.Docx; return true;
            case "xlsx": format = OfficeFormat.Xlsx; return true;
            case "pptx": format = OfficeFormat.Pptx; return true;
            default: format = default; return false;
        }
    }

    public static string Extension(OfficeFormat format) => format switch
    {
        OfficeFormat.Docx => ".docx",
        OfficeFormat.Xlsx => ".xlsx",
        _ => ".pptx",
    };

    public static string ContentType(OfficeFormat format) => format switch
    {
        OfficeFormat.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        OfficeFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    /// <param name="title">
    /// Fallback title: the document title in the file properties and the title slide of a deck when
    /// the text has no leading <c>#</c> heading.
    /// </param>
    public static byte[] Create(OfficeFormat format, string? markdown, string? title = null)
    {
        var text = XmlSafe(markdown ?? string.Empty);
        if (text.Length > MaxMarkdownChars)
        {
            text = text[..SafeCut(text, MaxMarkdownChars)];
        }

        var fallbackTitle = XmlSafe(string.IsNullOrWhiteSpace(title) ? "Документ" : title.Trim());
        if (fallbackTitle.Length > 200) fallbackTitle = fallbackTitle[..SafeCut(fallbackTitle, 200)];

        return format switch
        {
            OfficeFormat.Docx => DocxWriter.Write(MarkdownBlocks.Parse(text), fallbackTitle),
            OfficeFormat.Xlsx => XlsxWriter.Write(text, fallbackTitle),
            _ => PptxWriter.Write(MarkdownBlocks.Parse(text), fallbackTitle),
        };
    }

    /// <summary>
    /// Drops characters XML 1.0 cannot carry (model output can contain control characters and lone
    /// surrogates); every writer would otherwise throw while saving.
    /// </summary>
    internal static string XmlSafe(string value)
    {
        var clean = true;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            if (!XmlConvert.IsXmlChar(ch))
            {
                clean = false;
                break;
            }
        }
        if (clean) return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(ch).Append(value[++i]);
                continue;
            }
            if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A cut position at or below <paramref name="max"/> that does not split a surrogate pair.</summary>
    internal static int SafeCut(string value, int max)
    {
        if (max >= value.Length) return value.Length;
        if (max <= 0) return 0;
        return char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
    }

    /// <summary>An absolute http(s)/mailto URI for a Markdown link target, or null when it is not one.</summary>
    internal static Uri? LinkUri(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 2000) return null;
        var value = target.Trim();
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" or "mailto" ? uri : null;
    }

    /// <summary>"ru-RU" when the text has Cyrillic letters, "en-US" otherwise — for spell-checking.</summary>
    internal static string LanguageOf(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= 'Ѐ' and <= 'ӿ') return "ru-RU";
        }
        return "en-US";
    }

    /// <summary>
    /// Table column widths summing to <paramref name="total"/>: each column first gets room for its
    /// longest unbreakable token (no word broken mid-word when that can be avoided), the remaining room
    /// goes to the columns whose longest line needs more.
    /// </summary>
    /// <param name="charWidth">Average character width, in the same unit as <paramref name="total"/>.</param>
    /// <param name="padding">Cell margins (both sides), in the same unit.</param>
    /// <param name="fits">False when even the longest tokens do not fit and had to be squeezed.</param>
    internal static double[] ColumnWidths(MdTable table, double total, double charWidth, double padding, out bool fits)
    {
        var columns = table.ColumnCount;
        var minimum = new double[columns];
        var desired = new double[columns];
        var rows = table.Rows;
        for (var r = 0; r < rows.Count; r++)
        {
            // The header is bold, hence a little wider.
            var scale = r == 0 ? 1.1 : 1.0;
            for (var c = 0; c < columns && c < rows[r].Count; c++)
            {
                var text = rows[r][c];
                var word = TokenRegex().Matches(text).Select(m => m.Length).DefaultIfEmpty(0).Max();
                var line = text.Split('\n').Max(l => l.Length);
                minimum[c] = Math.Max(minimum[c], Math.Min(word, 30) * charWidth * scale + padding);
                desired[c] = Math.Max(desired[c], Math.Min(line, 60) * charWidth * scale + padding);
            }
        }
        for (var c = 0; c < columns; c++)
        {
            minimum[c] = Math.Max(minimum[c], 3 * charWidth + padding);
            desired[c] = Math.Max(desired[c], minimum[c]);
        }

        var minimumSum = minimum.Sum();
        fits = minimumSum <= total;
        if (!fits) return minimum.Select(m => m * total / minimumSum).ToArray();
        var desiredSum = desired.Sum();
        if (desiredSum <= total) return desired.Select(d => d * total / desiredSum).ToArray();

        var spare = total - minimumSum;
        var wanted = desired.Select((d, i) => d - minimum[i]).ToArray();
        var wantedSum = wanted.Sum();
        return minimum.Select((m, i) => m + spare * wanted[i] / wantedSum).ToArray();
    }

    // A run of non-space characters; spaces between digits ("1 290,50", "+7 999 123") do not break it.
    [System.Text.RegularExpressions.GeneratedRegex(@"\S+(?:(?<=\d)[  ](?=\d)\S+)*")]
    private static partial System.Text.RegularExpressions.Regex TokenRegex();

    /// <summary>Visible text of a link run: "text (url)" when the target cannot be a real hyperlink.</summary>
    internal static string LinkFallbackText(MdRun run) =>
        run.Link is null || run.Text == run.Link || run.Link.StartsWith('#') ? run.Text : $"{run.Text} ({run.Link})";
}

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace ConexyAI.Service;

// RAG: добавлено 2026-09-17
/// <summary>Extracts plain text from uploaded documents (TXT, MD, DOCX, XLSX, PPTX, PDF).</summary>
public static class DocumentParser
{
    // OFFICE_FORMATS: добавлено 2026-09-23 — formats that are text already.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".html", ".css", ".sql",
    };

    /// <summary>
    /// OFFICE_FORMATS: true when <see cref="Parse"/> actually understands the file, as opposed to
    /// its best-effort "read the bytes as UTF-8" fallback — which for a zip, an image or a legacy
    /// binary .doc produces garbage that must not be shown to a model.
    /// </summary>
    public static bool CanExtract(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return TextExtensions.Contains(ext) || ext.ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx" or ".pdf";
    }

    public static string Parse(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;

        if (TextExtensions.Contains(ext))
            return Encoding.UTF8.GetString(bytes);

        return ext switch
        {
            ".docx" => ParseDocx(bytes),
            ".xlsx" => ParseXlsx(bytes),
            ".pptx" => ParsePptx(bytes),
            ".pdf" => ParsePdf(bytes),

            _ => Encoding.UTF8.GetString(bytes) // best-effort: treat unknown as text
        };
    }

    // OFFICE_FORMATS: переписано 2026-09-23 — раньше все w:t склеивались через пробел в одну
    // строку: абзацы, заголовки и ячейки таблиц сливались, и модель не видела структуру документа.
    private static string ParseDocx(byte[] bytes)
    {
        try
        {
            using var zip = OpenZip(bytes);
            var document = LoadXml(zip, "word/document.xml");
            var body = document?.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body is null)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var element in body.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "p":
                        sb.AppendLine(DocxParagraphText(element));
                        break;
                    case "tbl":
                        foreach (var row in element.Descendants().Where(e => e.Name.LocalName == "tr"))
                        {
                            var cells = row.Elements()
                                .Where(e => e.Name.LocalName == "tc")
                                .Select(tc => string.Join(" ", tc.Descendants().Where(e => e.Name.LocalName == "p").Select(DocxParagraphText)).Trim());
                            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                        }
                        sb.AppendLine();
                        break;
                    case "sdt":
                        // Content controls (tables of contents, form fields) wrap ordinary paragraphs.
                        foreach (var paragraph in element.Descendants().Where(e => e.Name.LocalName == "p"))
                        {
                            sb.AppendLine(DocxParagraphText(paragraph));
                        }
                        break;
                }
            }

            return CollapseBlankLines(sb.ToString());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DocxParagraphText(XElement paragraph)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            switch (node.Name.LocalName)
            {
                case "t": sb.Append(node.Value); break;
                case "tab": sb.Append('\t'); break;
                case "br" or "cr": sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    // OFFICE_FORMATS: добавлено 2026-09-23 — every sheet as a Markdown-like table, so a model reads
    // rows and columns rather than a flat list of values.
    private static string ParseXlsx(byte[] bytes)
    {
        try
        {
            using var zip = OpenZip(bytes);
            var shared = LoadXml(zip, "xl/sharedStrings.xml")?.Root?.Elements()
                .Where(e => e.Name.LocalName == "si")
                .Select(si => string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)))
                .ToList() ?? new List<string>();

            var workbook = LoadXml(zip, "xl/workbook.xml");
            var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels")?.Root?.Elements()
                .ToDictionary(e => (string?)e.Attribute("Id") ?? string.Empty, e => (string?)e.Attribute("Target") ?? string.Empty)
                ?? new Dictionary<string, string>();

            var sb = new StringBuilder();
            var sheets = workbook?.Descendants().Where(e => e.Name.LocalName == "sheet") ?? Enumerable.Empty<XElement>();
            foreach (var sheet in sheets)
            {
                var relId = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? string.Empty;
                if (!rels.TryGetValue(relId, out var target))
                    continue;
                var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                var sheetXml = LoadXml(zip, path);
                if (sheetXml is null)
                    continue;

                sb.AppendLine($"## Лист: {(string?)sheet.Attribute("name")}");
                foreach (var row in sheetXml.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    var cells = new SortedDictionary<int, string>();
                    foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        var column = ColumnIndex((string?)cell.Attribute("r"));
                        cells[column < 0 ? cells.Count : column] = XlsxCellText(cell, shared);
                    }
                    if (cells.Count == 0 || cells.Values.All(string.IsNullOrWhiteSpace))
                        continue;

                    var width = cells.Keys.Max() + 1;
                    var values = Enumerable.Range(0, width).Select(i => cells.TryGetValue(i, out var v) ? v : string.Empty);
                    sb.AppendLine("| " + string.Join(" | ", values) + " |");
                }
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string XlsxCellText(XElement cell, IReadOnlyList<string> shared)
    {
        var type = (string?)cell.Attribute("t");
        var value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
        return type switch
        {
            "s" when int.TryParse(value, out var index) && index >= 0 && index < shared.Count => shared[index],
            "inlineStr" => string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)),
            "b" => value == "1" ? "TRUE" : "FALSE",
            _ => value ?? string.Empty,
        };
    }

    /// <summary>"C12" → 2. Returns -1 when the reference is missing.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return -1;
        var index = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter))
        {
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return index - 1;
    }

    // OFFICE_FORMATS: добавлено 2026-09-23 — slides in presentation order, one paragraph per line.
    private static string ParsePptx(byte[] bytes)
    {
        try
        {
            using var zip = OpenZip(bytes);
            var rels = LoadXml(zip, "ppt/_rels/presentation.xml.rels")?.Root?.Elements()
                .ToDictionary(e => (string?)e.Attribute("Id") ?? string.Empty, e => (string?)e.Attribute("Target") ?? string.Empty)
                ?? new Dictionary<string, string>();
            var slideIds = LoadXml(zip, "ppt/presentation.xml")?.Descendants()
                .Where(e => e.Name.LocalName == "sldId")
                .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Length > 0)?.Value ?? string.Empty)
                .ToList() ?? new List<string>();

            var sb = new StringBuilder();
            var number = 0;
            foreach (var relId in slideIds)
            {
                if (!rels.TryGetValue(relId, out var target))
                    continue;
                var slide = LoadXml(zip, "ppt/" + target.TrimStart('/').Replace("../", string.Empty));
                if (slide is null)
                    continue;

                sb.AppendLine($"## Слайд {++number}");
                foreach (var paragraph in slide.Descendants().Where(e => e.Name.LocalName == "p" && e.Name.NamespaceName.Contains("drawingml")))
                {
                    var text = string.Concat(paragraph.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value));
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine(text);
                }
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ZipArchive OpenZip(byte[] bytes) => new(new MemoryStream(bytes), ZipArchiveMode.Read);

    private static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string CollapseBlankLines(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

    private static string ParsePdf(byte[] bytes)
    {
        // PdfPig (MIT) extracts text in reading order, honoring ToUnicode CMaps, so
        // Cyrillic and multi-column/multi-page PDFs are handled far better than the
        // previous regex-based parser.
        try
        {
            using var document = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}

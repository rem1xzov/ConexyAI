using System.Globalization;
using System.IO.Compression;
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
/// Builds .docx / .xlsx / .pptx files from Markdown, with no third-party dependency: each format is
/// a zip of a handful of OOXML parts, written here directly. Used by the agents' <c>create_document</c>
/// tool and by the "download as…" export available for every model's answer.
/// </summary>
public static class OfficeDocumentWriter
{
    public const int MaxMarkdownChars = 2_000_000;

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

    /// <param name="title">Fallback title (slide title when the text has no heading).</param>
    public static byte[] Create(OfficeFormat format, string? markdown, string? title = null)
    {
        var text = markdown ?? string.Empty;
        if (text.Length > MaxMarkdownChars)
        {
            text = text[..MaxMarkdownChars];
        }

        var fallbackTitle = string.IsNullOrWhiteSpace(title) ? "Документ" : title.Trim();
        return format switch
        {
            OfficeFormat.Docx => Docx(MarkdownBlocks.Parse(text)),
            OfficeFormat.Xlsx => Xlsx(text),
            _ => Pptx(MarkdownBlocks.Parse(text), fallbackTitle),
        };
    }

    // ------------------------------------------------------------------ package

    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    private const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private static byte[] Package(IEnumerable<(string Path, string Xml)> parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in parts)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(false).GetBytes(XmlHeader + xml);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    private static string Relationships(IEnumerable<(string Id, string Type, string Target)> rels)
    {
        var sb = new StringBuilder($"<Relationships xmlns=\"{RelsNs}\">");
        foreach (var (id, type, target) in rels)
        {
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{target}\"/>");
        }
        return sb.Append("</Relationships>").ToString();
    }

    private static string ContentTypes(IEnumerable<(string Part, string Type)> overrides)
    {
        var sb = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        foreach (var (part, type) in overrides)
        {
            sb.Append($"<Override PartName=\"{part}\" ContentType=\"{type}\"/>");
        }
        return sb.Append("</Types>").ToString();
    }

    /// <summary>XML-escapes text and drops characters XML cannot carry (model output can contain them).</summary>
    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(ch).Append(value[++i]);
                continue;
            }
            if (!XmlConvert.IsXmlChar(ch)) continue;
            sb.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ docx

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    // A4 with 3 cm / 1.5 cm margins: 11906 - 1701 - 850 twips of text width.
    private const int DocxTextWidth = 9355;

    private static byte[] Docx(IReadOnlyList<MdBlock> blocks)
    {
        var body = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdHeading h:
                    body.Append(DocxParagraph(h.Runs, style: "Heading" + Math.Clamp(h.Level, 1, 3)));
                    break;
                case MdParagraph p:
                    body.Append(DocxParagraph(p.Runs));
                    break;
                case MdListItem li:
                    var marker = new MdRun((li.Ordered ? li.Number + "." : "•") + "\t", false);
                    body.Append(DocxParagraph(li.Runs.Prepend(marker), indent: 360 * (li.Depth + 1), hanging: 360));
                    break;
                case MdTable t:
                    body.Append(DocxTable(t));
                    // Word expects a paragraph between a table and whatever follows it.
                    body.Append("<w:p/>");
                    break;
                case MdCode c:
                    foreach (var line in c.Text.Split('\n'))
                    {
                        body.Append(DocxParagraph(new[] { new MdRun(line, false) }, mono: true));
                    }
                    break;
            }
        }
        if (body.Length == 0) body.Append("<w:p/>");

        var document =
            $"<w:document xmlns:w=\"{W}\"><w:body>{body}" +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
            "<w:pgMar w:top=\"1134\" w:right=\"850\" w:bottom=\"1134\" w:left=\"1701\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/>" +
            "</w:sectPr></w:body></w:document>";

        return Package(new[]
        {
            ("[Content_Types].xml", ContentTypes(new[]
            {
                ("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
                ("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"),
            })),
            ("_rels/.rels", Relationships(new[] { ("rId1", OfficeDocRel, "word/document.xml") })),
            ("word/_rels/document.xml.rels", Relationships(new[]
            {
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml"),
            })),
            ("word/document.xml", document),
            ("word/styles.xml", DocxStyles),
        });
    }

    private static string DocxParagraph(
        IEnumerable<MdRun> runs, string? style = null, int indent = 0, int hanging = 0, bool mono = false)
    {
        var sb = new StringBuilder("<w:p>");
        if (style != null || indent > 0)
        {
            sb.Append("<w:pPr>");
            if (style != null) sb.Append($"<w:pStyle w:val=\"{style}\"/>");
            if (indent > 0) sb.Append($"<w:ind w:left=\"{indent}\" w:hanging=\"{hanging}\"/>");
            sb.Append("</w:pPr>");
        }

        foreach (var run in runs)
        {
            sb.Append("<w:r>");
            if (run.Bold || mono)
            {
                // rPr children follow the schema order: rFonts, b, sz.
                sb.Append("<w:rPr>");
                if (mono) sb.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>");
                if (run.Bold) sb.Append("<w:b/>");
                if (mono) sb.Append("<w:sz w:val=\"20\"/>");
                sb.Append("</w:rPr>");
            }

            var segments = run.Text.Split('\t');
            for (var i = 0; i < segments.Length; i++)
            {
                if (i > 0) sb.Append("<w:tab/>");
                if (segments[i].Length > 0) sb.Append($"<w:t xml:space=\"preserve\">{Esc(segments[i])}</w:t>");
            }
            sb.Append("</w:r>");
        }

        return sb.Append("</w:p>").ToString();
    }

    private static string DocxTable(MdTable table)
    {
        var columns = Math.Max(1, table.Rows.Max(r => r.Count));
        var width = DocxTextWidth / columns;
        var sb = new StringBuilder("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>");
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            sb.Append($"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"A6A6A6\"/>");
        }
        sb.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
        for (var c = 0; c < columns; c++) sb.Append($"<w:gridCol w:w=\"{width}\"/>");
        sb.Append("</w:tblGrid>");

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var header = r == 0;
            sb.Append(header ? "<w:tr><w:trPr><w:tblHeader/></w:trPr>" : "<w:tr>");
            for (var c = 0; c < columns; c++)
            {
                var cell = c < table.Rows[r].Count ? table.Rows[r][c] : string.Empty;
                sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>");
                if (header) sb.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"E8EEF7\"/>");
                sb.Append("</w:tcPr>");
                sb.Append(DocxParagraph(new[] { new MdRun(cell, header) }));
                sb.Append("</w:tc>");
            }
            sb.Append("</w:tr>");
        }

        return sb.Append("</w:tbl>").ToString();
    }

    private static readonly string DocxStyles =
        $"<w:styles xmlns:w=\"{W}\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr>" +
        "<w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/>" +
        "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"ru-RU\" w:eastAsia=\"en-US\" w:bidi=\"ar-SA\"/>" +
        "</w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        DocxHeadingStyle(1, "heading 1", 32, "1F3864", 360) +
        DocxHeadingStyle(2, "heading 2", 28, "2F5496", 240) +
        DocxHeadingStyle(3, "heading 3", 24, "2F5496", 200) +
        "</w:styles>";

    private static string DocxHeadingStyle(int level, string name, int size, string color, int before) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"{name}\"/>" +
        "<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>" +
        $"<w:pPr><w:keepNext/><w:spacing w:before=\"{before}\" w:after=\"120\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
        $"<w:rPr><w:b/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";

    // ------------------------------------------------------------------ xlsx

    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MaxCellChars = 32_767;

    private sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

    private static byte[] Xlsx(string markdown)
    {
        var sheets = SheetsFrom(markdown);
        var parts = new List<(string, string)>
        {
            ("[Content_Types].xml", ContentTypes(
                new[] { ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                        ("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml") }
                .Concat(sheets.Select((_, i) => ($"/xl/worksheets/sheet{i + 1}.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))))),
            ("_rels/.rels", Relationships(new[] { ("rId1", OfficeDocRel, "xl/workbook.xml") })),
        };

        var workbook = new StringBuilder($"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><bookViews><workbookView/></bookViews><sheets>");
        var rels = new List<(string, string, string)>();
        for (var i = 0; i < sheets.Count; i++)
        {
            workbook.Append($"<sheet name=\"{Esc(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            rels.Add(($"rId{i + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i + 1}.xml"));
            parts.Add(($"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i])));
        }
        workbook.Append("</sheets></workbook>");
        rels.Add(($"rId{sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml"));

        parts.Add(("xl/workbook.xml", workbook.ToString()));
        parts.Add(("xl/_rels/workbook.xml.rels", Relationships(rels)));
        parts.Add(("xl/styles.xml", XlsxStyles));
        return Package(parts);
    }

    /// <summary>
    /// Markdown tables become sheets (named after the heading above them). Text without tables is
    /// read as CSV/TSV, because that is what a model hands over when asked for "a spreadsheet".
    /// </summary>
    private static List<Sheet> SheetsFrom(string markdown)
    {
        var sheets = new List<Sheet>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastHeading = null;
        foreach (var block in MarkdownBlocks.Parse(markdown))
        {
            if (block is MdHeading h) lastHeading = MarkdownBlocks.PlainText(h.Runs);
            if (block is MdTable t)
            {
                sheets.Add(new Sheet(SheetName(lastHeading ?? $"Таблица {sheets.Count + 1}", names), t.Rows));
                lastHeading = null;
            }
        }

        if (sheets.Count == 0)
        {
            sheets.Add(new Sheet(SheetName("Лист1", names), ParseDelimited(markdown)));
        }
        return sheets;
    }

    private static List<IReadOnlyList<string>> ParseDelimited(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return new List<IReadOnlyList<string>>();

        var first = lines[0];
        var delimiter = first.Contains('\t') ? '\t'
            : first.Count(c => c == ';') > first.Count(c => c == ',') ? ';'
            : ',';
        return lines.Select(l => (IReadOnlyList<string>)SplitDelimited(l, delimiter)).ToList();
    }

    private static List<string> SplitDelimited(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == delimiter) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static string SheetName(string raw, HashSet<string> taken)
    {
        var cleaned = new string(raw.Where(c => "[]:*?/\\".IndexOf(c) < 0 && !char.IsControl(c)).ToArray()).Trim().Trim('\'');
        if (cleaned.Length == 0) cleaned = "Лист";
        if (cleaned.Length > 31) cleaned = cleaned[..31];

        var name = cleaned;
        for (var n = 2; !taken.Add(name); n++)
        {
            var suffix = $" ({n})";
            name = (cleaned.Length + suffix.Length > 31 ? cleaned[..(31 - suffix.Length)] : cleaned) + suffix;
        }
        return name;
    }

    private static string Worksheet(Sheet sheet)
    {
        var sb = new StringBuilder($"<worksheet xmlns=\"{S}\">");
        if (sheet.Rows.Count == 0)
        {
            return sb.Append("<sheetData/></worksheet>").ToString();
        }

        // The first row is a header: frozen and bold.
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");

        var columns = sheet.Rows.Max(r => r.Count);
        sb.Append("<cols>");
        for (var c = 0; c < columns; c++)
        {
            var longest = sheet.Rows.Max(r => c < r.Count ? r[c].Length : 0);
            var width = Math.Clamp(longest + 2, 8, 60);
            sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width}\" customWidth=\"1\"/>");
        }
        sb.Append("</cols><sheetData>");

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < sheet.Rows[r].Count; c++)
            {
                var value = sheet.Rows[r][c];
                if (value.Length == 0) continue;
                var reference = ColumnName(c) + (r + 1);
                if (r > 0 && TryNumber(value, out var number))
                {
                    sb.Append($"<c r=\"{reference}\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                }
                else
                {
                    var text = value.Length > MaxCellChars ? value[..MaxCellChars] : value;
                    var style = r == 0 ? " s=\"1\"" : string.Empty;
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                }
            }
            sb.Append("</row>");
        }

        return sb.Append("</sheetData></worksheet>").ToString();
    }

    /// <summary>
    /// "1 234,5", "1234.5" and "-7" are numbers; "007", dates, phone numbers and "15%" stay text so a
    /// spreadsheet never silently rewrites an identifier.
    /// </summary>
    private static bool TryNumber(string raw, out double value)
    {
        value = 0;
        var s = raw.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty);
        if (s.Length == 0 || s.Length > 20) return false;
        if (s.Count(ch => ch == ',') == 1 && !s.Contains('.')) s = s.Replace(',', '.');
        if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^-?(0|[1-9]\d*)(\.\d+)?$")) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }
        return name;
    }

    private static readonly string XlsxStyles =
        $"<styleSheet xmlns=\"{S}\">" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    // ------------------------------------------------------------------ pptx

    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string PresentationNs = $"xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    // Beyond this many lines a 16:9 slide overflows at the body size below; the rest continues on
    // the next slide instead of running off the bottom.
    private const int MaxLinesPerSlide = 8;

    private sealed record SlideLine(IReadOnlyList<MdRun> Runs, int Depth, bool Bullet);

    private sealed record Slide(string Title, List<SlideLine> Lines);

    private static byte[] Pptx(IReadOnlyList<MdBlock> blocks, string fallbackTitle)
    {
        var slides = SlidesFrom(blocks, fallbackTitle);

        var parts = new List<(string, string)>
        {
            ("[Content_Types].xml", ContentTypes(new[]
            {
                ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
                ("/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"),
                ("/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"),
                ("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml"),
                ("/ppt/presProps.xml", "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml"),
                ("/ppt/viewProps.xml", "application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml"),
                ("/ppt/tableStyles.xml", "application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml"),
            }.Concat(slides.Select((_, i) => ($"/ppt/slides/slide{i + 1}.xml",
                "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"))))),
            ("_rels/.rels", Relationships(new[] { ("rId1", OfficeDocRel, "ppt/presentation.xml") })),
        };

        var slideIds = new StringBuilder();
        var presentationRels = new List<(string, string, string)>
        {
            ("rId1", RelBase + "slideMaster", "slideMasters/slideMaster1.xml"),
            ("rId2", RelBase + "theme", "theme/theme1.xml"),
            ("rId3", RelBase + "presProps", "presProps.xml"),
            ("rId4", RelBase + "viewProps", "viewProps.xml"),
            ("rId5", RelBase + "tableStyles", "tableStyles.xml"),
        };
        for (var i = 0; i < slides.Count; i++)
        {
            var relId = $"rId{i + 6}";
            slideIds.Append($"<p:sldId id=\"{256 + i}\" r:id=\"{relId}\"/>");
            presentationRels.Add((relId, RelBase + "slide", $"slides/slide{i + 1}.xml"));
            parts.Add(($"ppt/slides/slide{i + 1}.xml", SlideXml(slides[i])));
            parts.Add(($"ppt/slides/_rels/slide{i + 1}.xml.rels",
                Relationships(new[] { ("rId1", RelBase + "slideLayout", "../slideLayouts/slideLayout1.xml") })));
        }

        parts.Add(("ppt/presentation.xml",
            $"<p:presentation {PresentationNs} saveSubsetFonts=\"1\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{slideIds}</p:sldIdLst>" +
            "<p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
            "</p:presentation>"));
        parts.Add(("ppt/_rels/presentation.xml.rels", Relationships(presentationRels)));
        parts.Add(("ppt/slideMasters/slideMaster1.xml", SlideMaster));
        parts.Add(("ppt/slideMasters/_rels/slideMaster1.xml.rels", Relationships(new[]
        {
            ("rId1", RelBase + "slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("rId2", RelBase + "theme", "../theme/theme1.xml"),
        })));
        parts.Add(("ppt/slideLayouts/slideLayout1.xml", SlideLayout));
        parts.Add(("ppt/slideLayouts/_rels/slideLayout1.xml.rels",
            Relationships(new[] { ("rId1", RelBase + "slideMaster", "../slideMasters/slideMaster1.xml") })));
        parts.Add(("ppt/theme/theme1.xml", Theme));
        parts.Add(("ppt/presProps.xml", $"<p:presentationPr {PresentationNs}/>"));
        parts.Add(("ppt/viewProps.xml", $"<p:viewPr {PresentationNs}><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>"));
        parts.Add(("ppt/tableStyles.xml", $"<a:tblStyleLst xmlns:a=\"{A}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>"));
        return Package(parts);
    }

    /// <summary>Every <c>#</c>/<c>##</c> heading starts a slide; everything under it is the body.</summary>
    private static List<Slide> SlidesFrom(IReadOnlyList<MdBlock> blocks, string fallbackTitle)
    {
        var slides = new List<Slide>();
        Slide? current = null;
        Slide Current() => current ??= AddSlide(fallbackTitle);
        Slide AddSlide(string title)
        {
            var slide = new Slide(title.Length > 120 ? title[..120] + "…" : title, new List<SlideLine>());
            slides.Add(slide);
            return slide;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdHeading { Level: <= 2 } h:
                    current = AddSlide(MarkdownBlocks.PlainText(h.Runs));
                    break;
                case MdHeading h:
                    Current().Lines.Add(new SlideLine(h.Runs.Select(r => r with { Bold = true }).ToList(), 0, false));
                    break;
                case MdParagraph p:
                    Current().Lines.Add(new SlideLine(p.Runs, 0, false));
                    break;
                case MdListItem li:
                    var runs = li.Ordered ? li.Runs.Prepend(new MdRun(li.Number + ". ", false)).ToList() : li.Runs;
                    Current().Lines.Add(new SlideLine(runs, li.Depth, !li.Ordered));
                    break;
                case MdTable t:
                    for (var r = 0; r < t.Rows.Count; r++)
                    {
                        Current().Lines.Add(new SlideLine(new[] { new MdRun(string.Join("  |  ", t.Rows[r]), r == 0) }, 0, false));
                    }
                    break;
                case MdCode c:
                    foreach (var line in c.Text.Split('\n').Where(l => l.Trim().Length > 0))
                    {
                        Current().Lines.Add(new SlideLine(new[] { new MdRun(line, false) }, 0, false));
                    }
                    break;
            }
        }

        if (slides.Count == 0) AddSlide(fallbackTitle);

        // Split overfull slides into "(продолжение)" slides.
        var result = new List<Slide>();
        foreach (var slide in slides)
        {
            for (var i = 0; i == 0 || i < slide.Lines.Count; i += MaxLinesPerSlide)
            {
                result.Add(new Slide(
                    i == 0 ? slide.Title : slide.Title + " (продолжение)",
                    slide.Lines.Skip(i).Take(MaxLinesPerSlide).ToList()));
            }
        }
        return result;
    }

    private static string SlideXml(Slide slide)
    {
        var sb = new StringBuilder($"<p:sld {PresentationNs}><p:cSld><p:spTree>");
        sb.Append(GroupShapeHeader);
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
                  "<p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>");
        sb.Append($"<a:p><a:r><a:rPr lang=\"ru-RU\" dirty=\"0\"/><a:t>{Esc(slide.Title)}</a:t></a:r></a:p>");
        sb.Append("</p:txBody></p:sp>");

        if (slide.Lines.Count > 0)
        {
            sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
                      "<p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr><a:normAutofit/></a:bodyPr><a:lstStyle/>");
            foreach (var line in slide.Lines)
            {
                sb.Append("<a:p>");
                sb.Append(line.Bullet
                    ? $"<a:pPr lvl=\"{line.Depth}\"/>"
                    : "<a:pPr marL=\"0\" indent=\"0\"><a:buNone/></a:pPr>");
                foreach (var run in line.Runs)
                {
                    var bold = run.Bold ? " b=\"1\"" : string.Empty;
                    sb.Append($"<a:r><a:rPr lang=\"ru-RU\"{bold} dirty=\"0\"/><a:t>{Esc(run.Text)}</a:t></a:r>");
                }
                sb.Append("</a:p>");
            }
            sb.Append("</p:txBody></p:sp>");
        }

        return sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>").ToString();
    }

    private const string GroupShapeHeader =
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string Placeholder(int id, string name, string ph, long x, long y, long cx, long cy, string bodyPr) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
        $"<p:txBody>{bodyPr}<a:lstStyle/><a:p><a:endParaRPr lang=\"ru-RU\"/></a:p></p:txBody></p:sp>";

    private static string Level(int level, long marL, long indent, int size, string bullet) =>
        $"<a:lvl{level}pPr marL=\"{marL}\" indent=\"{indent}\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">" +
        "<a:spcBef><a:spcPts val=\"600\"/></a:spcBef>" + bullet +
        $"<a:defRPr sz=\"{size}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
        "<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl" + level + "pPr>";

    private static readonly string SlideMaster =
        $"<p:sldMaster {PresentationNs}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>" +
        GroupShapeHeader +
        Placeholder(2, "Title Placeholder 1", "<p:ph type=\"title\"/>", 838200, 365125, 10515600, 1325563,
            "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"ctr\"><a:normAutofit/></a:bodyPr>") +
        Placeholder(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", 838200, 1825625, 10515600, 4351338,
            "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"><a:normAutofit/></a:bodyPr>") +
        "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
        "<p:txStyles>" +
        "<p:titleStyle><a:lvl1pPr algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">" +
        "<a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/>" +
        "<a:defRPr sz=\"3600\" b=\"1\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill>" +
        "<a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle>" +
        Level(1, 228600, -228600, 2200, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>") +
        Level(2, 685800, -228600, 2000, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"–\"/>") +
        Level(3, 1143000, -228600, 1800, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>") +
        Level(4, 1600200, -228600, 1800, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"–\"/>") +
        "</p:bodyStyle>" +
        "<p:otherStyle>" + Level(1, 0, 0, 1800, "<a:buNone/>") + "</p:otherStyle>" +
        "</p:txStyles></p:sldMaster>";

    private static readonly string SlideLayout =
        $"<p:sldLayout {PresentationNs} type=\"obj\" preserve=\"1\"><p:cSld name=\"Title and Content\"><p:spTree>" +
        GroupShapeHeader +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr>" +
        "<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"ru-RU\"/></a:p></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr>" +
        "<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"ru-RU\"/></a:p></p:txBody></p:sp>" +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string SolidFill(string inner) => $"<a:solidFill>{inner}</a:solidFill>";

    private static readonly string Theme =
        $"<a:theme xmlns:a=\"{A}\" name=\"Conexy\"><a:themeElements>" +
        "<a:clrScheme name=\"Conexy\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"1F3864\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>" +
        "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
        "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Conexy\">" +
        "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Conexy\">" +
        "<a:fillStyleLst>" + string.Concat(Enumerable.Repeat(SolidFill("<a:schemeClr val=\"phClr\"/>"), 3)) + "</a:fillStyleLst>" +
        "<a:lnStyleLst>" + string.Concat(new[] { 6350, 12700, 19050 }.Select(w =>
            $"<a:ln w=\"{w}\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">{SolidFill("<a:schemeClr val=\"phClr\"/>")}<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>")) +
        "</a:lnStyleLst>" +
        "<a:effectStyleLst>" + string.Concat(Enumerable.Repeat("<a:effectStyle><a:effectLst/></a:effectStyle>", 3)) + "</a:effectStyleLst>" +
        "<a:bgFillStyleLst>" + string.Concat(Enumerable.Repeat(SolidFill("<a:schemeClr val=\"phClr\"/>"), 3)) + "</a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
}

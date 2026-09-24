using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace ConexyAI.Service.Office;

// OFFICE_OPENXML: добавлено 2026-09-24 — .xlsx через ClosedXML: каждая Markdown-таблица становится
// листом (уникальное имя ≤ 31 символа без запрещённых знаков), шапка жирная с заливкой, рамки,
// закреплённая строка, автофильтр, типизированные ячейки (числа, проценты, валюта, даты, логические,
// формулы) и ширина колонок по содержимому с разумным потолком.
internal static class XlsxWriter
{
    private const int MaxCellChars = 32_767;
    private const double MinColumnWidth = 8;
    private const double MaxColumnWidth = 60;
    private const double TextSheetWidth = 100;
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#D9E2F3");
    private static readonly XLColor BorderColor = XLColor.FromHtml("#A6A6A6");

    public static byte[] Write(string markdown, string fallbackTitle)
    {
        var blocks = MarkdownBlocks.Parse(markdown);
        var commaDecimalDefault = OfficeDocumentWriter.LanguageOf(markdown) == "ru-RU";

        using var workbook = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastHeading = null;
        var tables = 0;
        foreach (var block in blocks)
        {
            if (block is MdHeading heading) lastHeading = MarkdownBlocks.PlainText(heading.Runs);
            if (block is not MdTable table) continue;

            tables++;
            var sheet = workbook.Worksheets.Add(SheetName(lastHeading ?? $"Таблица {tables}", names));
            WriteGrid(sheet, GridFrom(table), commaDecimalDefault);
            lastHeading = null;
        }

        if (tables == 0)
        {
            var delimited = blocks.All(b => b is MdParagraph) ? ParseDelimited(markdown) : null;
            if (delimited is not null)
            {
                WriteGrid(workbook.Worksheets.Add(SheetName("Лист1", names)), delimited, commaDecimalDefault);
            }
            else
            {
                WriteTextSheet(workbook.Worksheets.Add(SheetName(fallbackTitle, names)), blocks);
            }
        }

        var title = blocks.OfType<MdHeading>().Select(h => MarkdownBlocks.PlainText(h.Runs)).FirstOrDefault() ?? fallbackTitle;
        workbook.Properties.Title = title;
        workbook.Properties.Author = OfficeDocumentWriter.Creator;
        workbook.Properties.LastModifiedBy = OfficeDocumentWriter.Creator;
        workbook.Properties.Created = DateTime.UtcNow;
        workbook.Properties.Modified = DateTime.UtcNow;

        var evaluate = false;
        if (workbook.Worksheets.Any(s => s.CellsUsed(c => c.HasFormula).Any()))
        {
            workbook.FullCalculationOnLoad = true;
            evaluate = CanCacheFormulaResults(workbook);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream, new SaveOptions { EvaluateFormulasBeforeSaving = evaluate });
        return stream.ToArray();
    }

    /// <summary>
    /// Cached results let previews (and our own parser) show formula values. They are written only when
    /// ClosedXML can compute every formula: a function it does not implement would be cached as #NAME?,
    /// and viewers that trust the cache would show that instead of recalculating.
    /// </summary>
    private static bool CanCacheFormulaResults(XLWorkbook workbook)
    {
        try
        {
            workbook.RecalculateAllFormulas();
            return !workbook.Worksheets
                .SelectMany(s => s.CellsUsed(c => c.HasFormula))
                .Any(c => c.CachedValue.IsError && c.CachedValue.GetError() == XLError.NameNotRecognized);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ grid sheets

    /// <summary>One sheet cell: its visible text, its source text (for formulas) and emphasis.</summary>
    private sealed record GridCell(string Text, string Raw, bool Bold, string? Link);

    private sealed record Grid(IReadOnlyList<IReadOnlyList<GridCell>> Rows, IReadOnlyList<MdAlign> Alignments);

    private static Grid GridFrom(MdTable table)
    {
        var rows = new List<IReadOnlyList<GridCell>>();
        for (var r = 0; r < table.Cells.Count; r++)
        {
            var cells = new List<GridCell>();
            for (var c = 0; c < table.Cells[r].Count; c++)
            {
                var runs = table.Cells[r][c];
                var link = runs.Count == 1 ? runs[0].Link : null;
                var text = link is not null
                    ? runs[0].Text
                    : string.Concat(runs.Select(OfficeDocumentWriter.LinkFallbackText));
                var raw = c < table.Raw[r].Count ? table.Raw[r][c] : text;
                var bold = runs.Count > 0 && runs.All(x => x.Bold || x.Text.Trim().Length == 0);
                cells.Add(new GridCell(text, raw, bold, link));
            }
            rows.Add(cells);
        }
        return new Grid(rows, table.Alignments);
    }

    private static void WriteGrid(IXLWorksheet sheet, Grid grid, bool commaDecimalDefault)
    {
        var rowCount = grid.Rows.Count;
        var columns = rowCount == 0 ? 0 : grid.Rows.Max(r => r.Count);
        if (rowCount == 0 || columns == 0) return;

        var widths = new double[columns];
        var wrap = new bool[columns];
        for (var c = 0; c < columns; c++)
        {
            var convention = ColumnConvention(grid, c, commaDecimalDefault);
            for (var r = 0; r < rowCount; r++)
            {
                if (c >= grid.Rows[r].Count) continue;
                var source = grid.Rows[r][c];
                var cell = sheet.Cell(r + 1, c + 1);
                var display = r == 0 ? SetText(cell, source.Text) : SetTyped(cell, source, convention);

                if (source.Bold && r > 0) cell.Style.Font.Bold = true;
                if (source.Link is not null && OfficeDocumentWriter.LinkUri(source.Link) is { } uri)
                {
                    cell.SetHyperlink(new XLHyperlink(uri));
                    cell.Style.Font.FontColor = XLColor.FromHtml("#0563C1");
                    cell.Style.Font.Underline = XLFontUnderlineValues.Single;
                }

                var longest = display.Split('\n').Max(l => l.Length);
                if (display.Contains('\n')) wrap[c] = true;
                var width = longest * (r == 0 ? 1.15 : 1.05) + 2;
                if (width > MaxColumnWidth) wrap[c] = true;
                widths[c] = Math.Max(widths[c], width);
            }
        }

        var table = sheet.Range(1, 1, rowCount, columns);
        table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.OutsideBorderColor = BorderColor;
        table.Style.Border.InsideBorderColor = BorderColor;
        table.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

        var header = sheet.Range(1, 1, 1, columns);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = HeaderFill;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Style.Alignment.WrapText = true;

        for (var c = 0; c < columns; c++)
        {
            var column = sheet.Column(c + 1);
            column.Width = Math.Clamp(widths[c], MinColumnWidth, MaxColumnWidth);
            if (wrap[c] && rowCount > 1) sheet.Range(2, c + 1, rowCount, c + 1).Style.Alignment.WrapText = true;

            var align = c < grid.Alignments.Count ? grid.Alignments[c] : MdAlign.None;
            if (align != MdAlign.None)
            {
                sheet.Range(1, c + 1, rowCount, c + 1).Style.Alignment.Horizontal = align switch
                {
                    MdAlign.Center => XLAlignmentHorizontalValues.Center,
                    MdAlign.Right => XLAlignmentHorizontalValues.Right,
                    _ => XLAlignmentHorizontalValues.Left,
                };
            }
        }

        sheet.SheetView.FreezeRows(1);
        if (rowCount > 1) table.SetAutoFilter();

        // Printing: every column on one page width, the header repeated on each page.
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 1);
        if (widths.Sum(w => Math.Clamp(w, MinColumnWidth, MaxColumnWidth)) > 100)
        {
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        }
    }

    private static string SetText(IXLCell cell, string text)
    {
        var value = text.Length > MaxCellChars ? text[..OfficeDocumentWriter.SafeCut(text, MaxCellChars)] : text;
        cell.SetValue(value);
        if (value.Contains('\n')) cell.Style.Alignment.WrapText = true;
        return value;
    }

    /// <returns>The text the cell displays (for the column width).</returns>
    private static string SetTyped(IXLCell cell, GridCell source, NumberConvention convention)
    {
        var parsed = CellParser.Parse(source.Text, source.Raw, convention);
        switch (parsed.Kind)
        {
            case CellKind.Formula:
                cell.FormulaA1 = parsed.Text;
                return source.Raw;
            case CellKind.Number:
            case CellKind.Percent:
            case CellKind.Currency:
                cell.SetValue(parsed.Number);
                if (parsed.Format is not null) cell.Style.NumberFormat.Format = parsed.Format;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                return source.Text;
            case CellKind.Date:
                cell.SetValue(parsed.Date);
                cell.Style.DateFormat.Format = parsed.Format;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                return source.Text;
            case CellKind.Boolean:
                cell.SetValue(parsed.Bool);
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                return parsed.Bool ? "TRUE" : "FALSE";
            default:
                return SetText(cell, source.Text);
        }
    }

    /// <summary>
    /// Whether "12,500" in this column means 12.5 or 12 500: decided by the unambiguous cells of the
    /// column ("1,234,567", "1 234,5", "12,5"), otherwise by the language of the document.
    /// </summary>
    private static NumberConvention ColumnConvention(Grid grid, int column, bool commaDecimalDefault)
    {
        for (var r = 1; r < grid.Rows.Count; r++)
        {
            if (column >= grid.Rows[r].Count) continue;
            var evidence = CellParser.Evidence(grid.Rows[r][column].Text);
            if (evidence != NumberConvention.Unknown) return evidence;
        }
        return commaDecimalDefault ? NumberConvention.CommaDecimal : NumberConvention.CommaThousands;
    }

    // ------------------------------------------------------------------ text / CSV fallbacks

    /// <summary>
    /// Plain CSV/TSV text becomes a sheet, because that is what a model sometimes hands over when
    /// asked for "a spreadsheet". Null when the text is not consistently delimited.
    /// </summary>
    private static Grid? ParseDelimited(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return null;

        foreach (var delimiter in new[] { '\t', ';', ',' })
        {
            var rows = lines.Select(l => SplitDelimited(l, delimiter)).ToList();
            var width = rows[0].Count;
            if (width < 2) continue;
            var consistent = rows.Count(r => r.Count == width);
            if (consistent < rows.Count * 0.8) continue;
            // Prose with a comma per line is not CSV.
            if (delimiter == ',' && rows.SelectMany(r => r).Average(c => c.Length) > 40) continue;

            return new Grid(
                rows.Select(r => (IReadOnlyList<GridCell>)r.Select(c => new GridCell(c, c, false, null)).ToList()).ToList(),
                Array.Empty<MdAlign>());
        }
        return null;
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
            else if (ch == '"' && cell.ToString().Trim().Length == 0) { cell.Clear(); quoted = true; }
            else if (ch == delimiter) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    /// <summary>A document without tables: one line per row, headings in bold.</summary>
    private static void WriteTextSheet(IXLWorksheet sheet, IReadOnlyList<MdBlock> blocks)
    {
        var row = 1;
        void Line(string text, Action<IXLStyle>? style = null, int indent = 0)
        {
            var cell = sheet.Cell(row++, 1);
            SetText(cell, text);
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            if (indent > 0) cell.Style.Alignment.Indent = Math.Min(indent, 15);
            style?.Invoke(cell.Style);
        }

        void Render(MdBlock block, bool quote)
        {
            switch (block)
            {
                case MdHeading h:
                    Line(Plain(h.Runs), s =>
                    {
                        s.Font.Bold = true;
                        s.Font.FontSize = h.Level switch { 1 => 16, 2 => 14, 3 => 12, _ => 11 };
                    });
                    break;
                case MdParagraph p:
                    Line(Plain(p.Runs), quote ? s => s.Font.Italic = true : null, quote ? 1 : 0);
                    break;
                case MdListItem li:
                    var marker = li.Checked is { } done ? (done ? "☑ " : "☐ ") : li.Ordered ? li.Number + ". " : "• ";
                    Line(marker + Plain(li.Runs), null, li.Depth + 1);
                    break;
                case MdCode c:
                    foreach (var codeLine in c.Text.Split('\n'))
                    {
                        Line(codeLine, s => s.Font.FontName = "Consolas");
                    }
                    break;
                case MdQuote q:
                    foreach (var inner in q.Blocks) Render(inner, quote: true);
                    break;
                case MdRule:
                    row++;
                    break;
            }
        }

        foreach (var block in blocks) Render(block, quote: false);
        sheet.Column(1).Width = TextSheetWidth;
    }

    private static string Plain(IEnumerable<MdRun> runs) => string.Concat(runs.Select(OfficeDocumentWriter.LinkFallbackText));

    /// <summary>
    /// Excel's sheet-name rules: at most 31 characters, none of <c>[]:*?/\</c>, not starting or ending
    /// with an apostrophe, unique (case-insensitively), and never the reserved "History".
    /// </summary>
    internal static string SheetName(string raw, HashSet<string> taken)
    {
        var cleaned = new string(raw.Where(c => "[]:*?/\\".IndexOf(c) < 0 && !char.IsControl(c)).ToArray()).Trim().Trim('\'').Trim();
        if (cleaned.Length == 0) cleaned = "Лист";
        if (cleaned.Length > 31) cleaned = cleaned[..OfficeDocumentWriter.SafeCut(cleaned, 31)].Trim();

        var name = cleaned;
        for (var n = 2; taken.Contains(name) || name.Equals("History", StringComparison.OrdinalIgnoreCase); n++)
        {
            var suffix = $" ({n})";
            var room = 31 - suffix.Length;
            name = (cleaned.Length > room ? cleaned[..OfficeDocumentWriter.SafeCut(cleaned, room)].TrimEnd() : cleaned) + suffix;
        }
        taken.Add(name);
        return name;
    }
}

internal enum NumberConvention
{
    Unknown,
    CommaDecimal,
    CommaThousands,
}

internal enum CellKind
{
    Text,
    Number,
    Percent,
    Currency,
    Date,
    Boolean,
    Formula,
}

internal readonly record struct ParsedCell(CellKind Kind, double Number, DateTime Date, bool Bool, string Text, string? Format);

/// <summary>
/// Reads a Markdown table cell as a typed spreadsheet value. Identifiers stay text: "007", phone
/// numbers, long digit strings (INN, account numbers) and anything with letters.
/// </summary>
internal static partial class CellParser
{
    private static readonly string[] CurrencyPrefixes = { "$", "€", "£", "₽", "¥" };

    private static readonly string[] CurrencySuffixes =
    {
        "руб.", "руб", "р.", "₽", "€", "$", "£", "¥", "₸", "USD", "EUR", "RUB", "GBP", "CNY",
    };

    public static ParsedCell Parse(string text, string raw, NumberConvention convention)
    {
        var trimmedRaw = raw.Trim();
        if (trimmedRaw.StartsWith('=') && FormulaParser.TryNormalize(trimmedRaw, out var formula))
        {
            return new ParsedCell(CellKind.Formula, 0, default, false, formula, null);
        }

        var value = text.Trim();
        if (value.Length == 0 || value.Length > 40) return Text(text);

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return new ParsedCell(CellKind.Boolean, 0, default, true, value, null);
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return new ParsedCell(CellKind.Boolean, 0, default, false, value, null);

        if (TryDate(value, out var date, out var dateFormat))
        {
            return new ParsedCell(CellKind.Date, 0, date, false, value, dateFormat);
        }

        return TryNumber(value, convention, out var parsed) ? parsed : Text(text);
    }

    /// <summary>Which decimal separator an unambiguous number in this cell uses, if any.</summary>
    public static NumberConvention Evidence(string text)
    {
        var core = NormalizeSpaces(text.Trim());
        core = core.Trim('+', '-', '(', ')', '%', ' ');
        foreach (var p in CurrencyPrefixes) if (core.StartsWith(p, StringComparison.Ordinal)) core = core[p.Length..].Trim();
        foreach (var s in CurrencySuffixes) if (core.EndsWith(s, StringComparison.OrdinalIgnoreCase)) core = core[..^s.Length].Trim();
        core = core.TrimStart('-');

        if (CommaThousandsRegex().IsMatch(core) && (core.Count(c => c == ',') > 1 || core.Contains('.'))) return NumberConvention.CommaThousands;
        if (SpaceGroupedRegex().IsMatch(core) && core.Contains(',')) return NumberConvention.CommaDecimal;
        if (PlainRegex().IsMatch(core) && core.Contains(','))
        {
            var decimals = core.Length - core.IndexOf(',') - 1;
            if (decimals != 3) return NumberConvention.CommaDecimal;
        }
        return NumberConvention.Unknown;
    }

    private static ParsedCell Text(string text) => new(CellKind.Text, 0, default, false, text, null);

    private static bool TryDate(string value, out DateTime date, out string format)
    {
        foreach (var (pattern, excel) in DatePatterns)
        {
            if (DateTime.TryParseExact(value, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                format = excel;
                return date.Year is >= 1900 and <= 9999;
            }
        }
        date = default;
        format = string.Empty;
        return false;
    }

    private static readonly (string Pattern, string Excel)[] DatePatterns =
    {
        ("yyyy-MM-dd", "yyyy-mm-dd"),
        ("yyyy-MM-dd HH:mm", "yyyy-mm-dd hh:mm"),
        ("yyyy-MM-dd HH:mm:ss", "yyyy-mm-dd hh:mm:ss"),
        ("yyyy-MM-ddTHH:mm", "yyyy-mm-dd hh:mm"),
        ("yyyy-MM-ddTHH:mm:ss", "yyyy-mm-dd hh:mm:ss"),
        ("dd.MM.yyyy", "dd.mm.yyyy"),
        ("d.M.yyyy", "dd.mm.yyyy"),
        ("dd.MM.yyyy HH:mm", "dd.mm.yyyy hh:mm"),
        ("dd.MM.yyyy HH:mm:ss", "dd.mm.yyyy hh:mm:ss"),
    };

    private static bool TryNumber(string value, NumberConvention convention, out ParsedCell parsed)
    {
        parsed = default;
        var s = NormalizeSpaces(value);

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1].Trim();
        }
        if (s.StartsWith('-') || s.StartsWith('+'))
        {
            negative ^= s[0] == '-';
            s = s[1..].TrimStart();
        }

        string? prefix = null, suffix = null;
        foreach (var p in CurrencyPrefixes)
        {
            if (!s.StartsWith(p, StringComparison.Ordinal)) continue;
            prefix = p;
            s = s[p.Length..].TrimStart();
            break;
        }
        if (prefix is not null && s.StartsWith('-'))
        {
            negative = !negative;
            s = s[1..].TrimStart();
        }

        var percent = false;
        if (prefix is null && s.EndsWith('%'))
        {
            percent = true;
            s = s[..^1].TrimEnd();
        }
        else if (prefix is null)
        {
            foreach (var x in CurrencySuffixes)
            {
                if (!s.EndsWith(x, StringComparison.OrdinalIgnoreCase)) continue;
                var head = s[..^x.Length];
                // "12USD" and "12 USD" are money; "12руб" too; but not a word ending in those letters.
                if (head.Length == 0 || !(char.IsDigit(head[^1]) || head[^1] == ' ')) continue;
                suffix = x;
                s = head.TrimEnd();
                break;
            }
        }

        // "$1,200" is written the English way whatever the column says.
        if (prefix is "$" or "£") convention = NumberConvention.CommaThousands;
        if (!TryCore(s, convention, out var number, out var decimals, out var grouped)) return false;
        if (negative) number = -number;

        var fraction = decimals > 0 ? "." + new string('0', Math.Min(decimals, 10)) : string.Empty;
        if (percent)
        {
            parsed = new ParsedCell(CellKind.Percent, number / 100, default, false, value, "0" + fraction + "%");
            return true;
        }
        if (prefix is not null || suffix is not null)
        {
            var symbol = (prefix ?? suffix)!.Replace("\"", string.Empty);
            var format = prefix is not null ? $"\"{symbol}\"#,##0{fraction}" : $"#,##0{fraction} \"{symbol}\"";
            parsed = new ParsedCell(CellKind.Currency, number, default, false, value, format);
            return true;
        }

        var numberFormat = grouped ? "#,##0" + fraction : decimals > 0 ? "0" + fraction : null;
        parsed = new ParsedCell(CellKind.Number, number, default, false, value, numberFormat);
        return true;
    }

    private static bool TryCore(string s, NumberConvention convention, out double number, out int decimals, out bool grouped)
    {
        number = 0;
        decimals = 0;
        grouped = false;
        if (s.Length == 0 || !char.IsDigit(s[0])) return false;

        string integer, fractionDigits;
        if (SpaceGroupedRegex().IsMatch(s))
        {
            grouped = true;
            var separator = s.LastIndexOfAny(new[] { ',', '.' });
            integer = (separator < 0 ? s : s[..separator]).Replace(" ", string.Empty);
            fractionDigits = separator < 0 ? string.Empty : s[(separator + 1)..];
        }
        else if (convention == NumberConvention.CommaThousands && CommaThousandsRegex().IsMatch(s) && s.Contains(','))
        {
            grouped = true;
            var dot = s.IndexOf('.');
            integer = (dot < 0 ? s : s[..dot]).Replace(",", string.Empty);
            fractionDigits = dot < 0 ? string.Empty : s[(dot + 1)..];
        }
        else if (DotThousandsRegex().IsMatch(s) && (s.Contains(',') || s.Count(c => c == '.') > 1))
        {
            // "1.234.567" / "1.234,56" — the continental way.
            grouped = true;
            var comma = s.IndexOf(',');
            integer = (comma < 0 ? s : s[..comma]).Replace(".", string.Empty);
            fractionDigits = comma < 0 ? string.Empty : s[(comma + 1)..];
        }
        else if (PlainRegex().IsMatch(s))
        {
            var separator = s.IndexOfAny(new[] { ',', '.' });
            integer = separator < 0 ? s : s[..separator];
            fractionDigits = separator < 0 ? string.Empty : s[(separator + 1)..];
            // Leading zeros and long digit strings are identifiers, not quantities.
            if (integer.Length > 1 && integer[0] == '0') return false;
            if (separator < 0 && integer.Length >= 11) return false;
        }
        else
        {
            return false;
        }

        if (integer.Length + fractionDigits.Length > 15) return false;
        decimals = fractionDigits.Length;
        var invariant = fractionDigits.Length > 0 ? integer + "." + fractionDigits : integer;
        return double.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    private static string NormalizeSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                ' ' or ' ' or ' ' or ' ' or ' ' or ' ' => ' ',
                '−' or '–' or '—' => '-',
                _ => ch,
            });
        }
        return sb.ToString().Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,3}(?: \d{3})+(?:[.,]\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex SpaceGroupedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,3}(?:,\d{3})+(?:\.\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex CommaThousandsRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,3}(?:\.\d{3})+(?:,\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex DotThousandsRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(?:[.,]\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex PlainRegex();
}

/// <summary>
/// Decides whether "=…" is a real spreadsheet formula (cell references, known functions, operators)
/// rather than text such as "=== Итого ===", and normalizes Russian spellings: СУММ → SUM, ";" → ",".
/// </summary>
internal static class FormulaParser
{
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "SUM", "SUMIF", "SUMIFS", "SUMPRODUCT", "PRODUCT", "AVERAGE", "AVERAGEIF", "AVERAGEIFS", "MIN", "MAX",
        "MINIFS", "MAXIFS", "COUNT", "COUNTA", "COUNTBLANK", "COUNTIF", "COUNTIFS", "ROUND", "ROUNDUP", "ROUNDDOWN",
        "INT", "ABS", "IF", "IFS", "IFERROR", "IFNA", "AND", "OR", "NOT", "XOR", "VLOOKUP", "HLOOKUP", "XLOOKUP",
        "LOOKUP", "INDEX", "MATCH", "XMATCH", "CHOOSE", "CONCATENATE", "CONCAT", "TEXTJOIN", "TEXT", "LEFT",
        "RIGHT", "MID", "LEN", "TRIM", "UPPER", "LOWER", "PROPER", "SUBSTITUTE", "REPLACE", "FIND", "SEARCH",
        "VALUE", "TODAY", "NOW", "DATE", "YEAR", "MONTH", "DAY", "WEEKDAY", "EOMONTH", "EDATE", "DATEDIF",
        "NETWORKDAYS", "WORKDAY", "HOUR", "MINUTE", "SECOND", "TIME", "POWER", "SQRT", "MOD", "EXP", "LN", "LOG",
        "LOG10", "PI", "RAND", "RANDBETWEEN", "MEDIAN", "MODE", "STDEV", "STDEV.S", "STDEV.P", "VAR", "VAR.S",
        "VAR.P", "LARGE", "SMALL", "RANK", "RANK.EQ", "PERCENTILE", "QUARTILE", "PMT", "FV", "PV", "NPV", "IRR",
        "XNPV", "XIRR", "RATE", "NPER", "CEILING", "FLOOR", "TRUNC", "SIGN", "ISBLANK", "ISNUMBER", "ISTEXT",
        "ISERROR", "NA", "OFFSET", "INDIRECT", "ROW", "ROWS", "COLUMN", "COLUMNS", "SUBTOTAL", "EXACT", "REPT",
        "CHAR", "CODE", "FIXED", "DAYS", "YEARFRAC",
    };

    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        ["СУММ"] = "SUM", ["СУММЕСЛИ"] = "SUMIF", ["СУММЕСЛИМН"] = "SUMIFS", ["СУММПРОИЗВ"] = "SUMPRODUCT",
        ["ПРОИЗВЕД"] = "PRODUCT", ["СРЗНАЧ"] = "AVERAGE", ["СРЗНАЧЕСЛИ"] = "AVERAGEIF", ["СРЗНАЧЕСЛИМН"] = "AVERAGEIFS",
        ["МИН"] = "MIN", ["МАКС"] = "MAX", ["СЧЕТ"] = "COUNT", ["СЧЕТЗ"] = "COUNTA", ["СЧИТАТЬПУСТОТЫ"] = "COUNTBLANK",
        ["СЧЕТЕСЛИ"] = "COUNTIF", ["СЧЕТЕСЛИМН"] = "COUNTIFS", ["ОКРУГЛ"] = "ROUND", ["ОКРУГЛВВЕРХ"] = "ROUNDUP",
        ["ОКРУГЛВНИЗ"] = "ROUNDDOWN", ["ЦЕЛОЕ"] = "INT", ["ЕСЛИ"] = "IF", ["ЕСЛИОШИБКА"] = "IFERROR", ["И"] = "AND",
        ["ИЛИ"] = "OR", ["НЕ"] = "NOT", ["ВПР"] = "VLOOKUP", ["ГПР"] = "HLOOKUP", ["ИНДЕКС"] = "INDEX",
        ["ПОИСКПОЗ"] = "MATCH", ["ВЫБОР"] = "CHOOSE", ["СЦЕПИТЬ"] = "CONCATENATE", ["СЦЕП"] = "CONCAT", ["ТЕКСТ"] = "TEXT",
        ["ЛЕВСИМВ"] = "LEFT", ["ПРАВСИМВ"] = "RIGHT", ["ПСТР"] = "MID", ["ДЛСТР"] = "LEN", ["СЖПРОБЕЛЫ"] = "TRIM",
        ["ЗНАЧЕН"] = "VALUE", ["СЕГОДНЯ"] = "TODAY", ["ТДАТА"] = "NOW", ["ДАТА"] = "DATE", ["ГОД"] = "YEAR",
        ["МЕСЯЦ"] = "MONTH", ["ДЕНЬ"] = "DAY", ["СТЕПЕНЬ"] = "POWER", ["КОРЕНЬ"] = "SQRT", ["ОСТАТ"] = "MOD",
        ["МЕДИАНА"] = "MEDIAN", ["СТАНДОТКЛОН"] = "STDEV", ["НАИБОЛЬШИЙ"] = "LARGE", ["НАИМЕНЬШИЙ"] = "SMALL",
        ["РАНГ"] = "RANK", ["ПЛТ"] = "PMT", ["БС"] = "FV", ["ПС"] = "PV", ["ЧПС"] = "NPV", ["ВСД"] = "IRR",
        ["ОКРВВЕРХ"] = "CEILING", ["ОКРВНИЗ"] = "FLOOR", ["ОТБР"] = "TRUNC", ["ПРОМЕЖУТОЧНЫЕ.ИТОГИ"] = "SUBTOTAL",
        ["ABS"] = "ABS",
    };

    /// <param name="raw">The cell text, starting with '='.</param>
    /// <param name="formula">The formula without the leading '=', in English Excel syntax.</param>
    public static bool TryNormalize(string raw, out string formula)
    {
        formula = string.Empty;
        var body = raw.Trim();
        if (!body.StartsWith('=') || body.Length < 2 || body.Length > 8000) return false;
        body = body[1..].Trim();
        if (body.Length == 0 || body.StartsWith('=')) return false;

        var sb = new StringBuilder(body.Length);
        var parens = 0;
        var references = 0;
        var functions = 0;
        var numbers = 0;
        var operators = 0;
        var i = 0;
        while (i < body.Length)
        {
            var ch = body[i];
            if (ch == '"')
            {
                var end = i + 1;
                while (end < body.Length)
                {
                    if (body[end] == '"' && end + 1 < body.Length && body[end + 1] == '"') { end += 2; continue; }
                    if (body[end] == '"') break;
                    end++;
                }
                if (end >= body.Length) return false;
                sb.Append(body, i, end - i + 1);
                i = end + 1;
                continue;
            }
            if (ch == '\'')
            {
                // 'Sheet name'!A1
                var end = body.IndexOf('\'', i + 1);
                if (end < 0 || end + 1 >= body.Length || body[end + 1] != '!') return false;
                sb.Append(body, i, end - i + 2);
                i = end + 2;
                continue;
            }
            if (char.IsLetter(ch) || ch == '$' || ch == '_')
            {
                var start = i;
                while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] is '$' or '_' or '.')) i++;
                var word = body[start..i];
                var next = i;
                while (next < body.Length && body[next] == ' ') next++;

                if (next < body.Length && body[next] == '(')
                {
                    var name = word.ToUpperInvariant().Replace('Ё', 'Е');
                    if (Russian.TryGetValue(name, out var english)) name = english;
                    if (!Functions.Contains(name)) return false;
                    sb.Append(name);
                    i = next;
                    functions++;
                    continue;
                }
                if (i < body.Length && body[i] == '!')
                {
                    if (!word.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
                    sb.Append(word).Append('!');
                    i++;
                    continue;
                }

                var upper = word.ToUpperInvariant();
                if (CellReference(upper) || (ColumnReference(upper) && NextToColon(body, start, i)))
                {
                    sb.Append(upper);
                    references++;
                    continue;
                }
                if (upper is "TRUE" or "FALSE")
                {
                    sb.Append(upper);
                    continue;
                }
                return false;
            }
            if (char.IsDigit(ch) || ch == '.')
            {
                var start = i;
                while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '.')) i++;
                sb.Append(body, start, i - start);
                numbers++;
                continue;
            }
            switch (ch)
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    if (--parens < 0) return false;
                    break;
                case ';':
                    // Russian Excel separates arguments with ';'.
                    sb.Append(',');
                    i++;
                    operators++;
                    continue;
                case '+' or '-' or '*' or '/' or '^' or '&' or '%' or '<' or '>' or '=' or ',' or ':':
                    operators++;
                    break;
                case ' ':
                    break;
                default:
                    return false;
            }
            sb.Append(ch);
            i++;
        }

        if (parens != 0) return false;
        if (references == 0 && functions == 0 && !(numbers >= 2 && operators >= 1)) return false;
        formula = sb.ToString();
        return true;
    }

    private static bool CellReference(string word)
    {
        var i = 0;
        if (i < word.Length && word[i] == '$') i++;
        var letters = 0;
        while (i < word.Length && word[i] is >= 'A' and <= 'Z') { i++; letters++; }
        if (letters is 0 or > 3) return false;
        if (i < word.Length && word[i] == '$') i++;
        var digits = 0;
        while (i < word.Length && char.IsAsciiDigit(word[i])) { i++; digits++; }
        return digits is > 0 and <= 7 && i == word.Length;
    }

    private static bool ColumnReference(string word)
    {
        var letters = word.TrimStart('$');
        return letters.Length is > 0 and <= 3 && letters.All(c => c is >= 'A' and <= 'Z');
    }

    private static bool NextToColon(string body, int start, int end) =>
        (start > 0 && body[start - 1] == ':') || (end < body.Length && body[end] == ':');
}

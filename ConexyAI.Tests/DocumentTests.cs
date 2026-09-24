using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Controller;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Office;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

// OFFICE_OPENXML / DOC_PARSER_LIMITS / TEXT_DECODING / RAG_DOCUMENTS: добавлено 2026-09-24
// Генерация .docx/.xlsx/.pptx (валидность по OpenXmlValidator, стили, типы ячеек, слайды), защита
// парсера от zip-бомб и раздувания строк (ревью H4), декодирование текстовых файлов и NUL (M6),
// страничное чтение документа (L6) и удаление документа из базы знаний (M9).
internal static class DocumentTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("docs: .docx/.xlsx/.pptx from rich Markdown pass OpenXmlValidator with zero errors", GeneratedFilesAreValidAsync);
        TestRegistry.Add("docs: .docx has Word styles, list numbering, a styled table, code and properties", DocxStructureAsync);
        TestRegistry.Add("docs: .xlsx cells are typed, header styled, frozen, filtered and fitted", XlsxTypedCellsAsync);
        TestRegistry.Add("docs: .xlsx number formats, sheet names and fallbacks", XlsxNumbersAndSheetNamesAsync);
        TestRegistry.Add("docs: .pptx has a title slide, a slide per heading, bullet levels and native tables", PptxStructureAsync);
        TestRegistry.Add("docs: xlsx with 1000 rows at column XFD is bounded in size and time (H4)", XlsxColumnBombAsync);
        TestRegistry.Add("docs: zip bombs, entry floods and oversize text are refused or cut (H4)", ZipLimitsAsync);
        TestRegistry.Add("docs: text files decode by BOM/UTF-16/1251 and never carry NUL (M6)", TextDecodingAsync);
        TestRegistry.Add("docs: truncation and chunking never split a surrogate pair (M6)", SurrogateSafetyAsync);
        TestRegistry.Add("docs: read_document_chunk returns bounded pages with a next index (L6)", ReadChunkPagesAsync);
        TestRegistry.Add("docs: DELETE /api/documents/{id} removes chunks, row and file, owner only (M9)", DeleteDocumentAsync);
        TestRegistry.Add("docs: a failed or rejected upload leaves no file on disk (M6)", UploadFailureLeavesNoFileAsync);
        TestRegistry.Add("docs: pathological Markdown is rendered in linear time", PathologicalMarkdownAsync);
    }

    private static Task PathologicalMarkdownAsync()
    {
        // The export endpoint takes up to 2 MB of Markdown from any user; closer searches must not be
        // quadratic on unmatched delimiters.
        var inputs = new Dictionary<string, string>
        {
            ["unmatched emphasis"] = string.Concat(Enumerable.Repeat("*a _b ~~c **d ", 40_000)),
            ["open brackets"] = new string('[', 200_000) + "x",
            ["links without targets"] = string.Concat(Enumerable.Repeat("[a](", 100_000)),
            ["backtick runs"] = string.Concat(Enumerable.Range(0, 100_000).Select(i => i % 2 == 0 ? "`a " : "``b ")),
        };
        foreach (var (name, markdown) in inputs)
        {
            var watch = Stopwatch.StartNew();
            var bytes = OfficeDocumentWriter.Create(OfficeFormat.Docx, markdown);
            watch.Stop();
            Assert(bytes.Length > 0 && watch.Elapsed < TimeSpan.FromSeconds(10), $"{name}: rendering took {watch.Elapsed}");
        }
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    internal const string RichMarkdown = """
        # Квартальный отчёт ConexyAI

        Краткая **сводка** по рынку, *курсив*, ~~старое~~, `inline code` и [ссылка](https://example.com/отчёт?q=1).

        ## Слайд 2: Ключевые показатели

        - Выручка выросла на **12%**
          - Москва: +15%
            - Центр: +18%
          - Регионы: +7%
        - Новых клиентов: 1 200
        1. Первый шаг
        2. Второй шаг
           1. Подшаг
        3. Третий шаг
        - [x] Сделано
        - [ ] Не сделано

        ## Цены

        | Кофейня | Капучино | Доля | Дата | Выручка | Активна | Код | Формула |
        |:---|---:|:---:|---|---|---|---|---|
        | Бодрость | 1 290,50 | 12% | 2026-09-23 | 1 200 ₽ | true | 007 | =B2*2 |
        | Утро | 250 | -3,5 % | 23.09.2026 | $1,200.50 | false | 0012 | =СУММ(B2:B3) |
        | Итого \| всё | -1 234,56 | 0% | 01.01.2026 | €15 | TRUE | +7 999 123-45-67 | === Итого === |

        > Цитата с **важной** мыслью.
        > - и пунктом

        ```csharp
        var x = 1;
        	Console.WriteLine("табуляция");
        ```

        ---

        ### Итоги

        Текст с emoji 😀, snake_case_name и символами <&> "кавычки".

        ## Slide 5: English slide

        Plain English paragraph with a https://conexy.ai link.
        """;

    private static readonly OfficeFormat[] AllFormats = { OfficeFormat.Docx, OfficeFormat.Xlsx, OfficeFormat.Pptx };

    /// <summary>Every validation error, described while the package is still open.</summary>
    internal static List<string> Validate(OfficeFormat format, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using OpenXmlPackage package = format switch
        {
            OfficeFormat.Docx => WordprocessingDocument.Open(stream, false),
            OfficeFormat.Xlsx => SpreadsheetDocument.Open(stream, false),
            _ => PresentationDocument.Open(stream, false),
        };
        var errors = new List<string>();
        foreach (var version in new[] { FileFormatVersions.Office2007, FileFormatVersions.Office2019 })
        {
            errors.AddRange(new OpenXmlValidator(version).Validate(package)
                .Select(e => $"[{version}] {e.Part?.Uri} {e.Path?.XPath}: {e.Description}"));
        }
        return errors;
    }

    private static Task GeneratedFilesAreValidAsync()
    {
        var samples = new[]
        {
            RichMarkdown,
            string.Empty,
            "просто текст",
            "# Только заголовок",
            "| a |\n|---|\n| 1 |",
            "## Пункты\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"{i}. пункт {i}")),
            "## Большая таблица\n| № | Название | Сумма |\n|---|---|---|\n" + string.Join("\n", Enumerable.Range(1, 60).Select(i => $"| {i} | Позиция {i} | {i * 1000} |")),
            "## Широкая\n|" + string.Join("|", Enumerable.Range(1, 15).Select(i => $" c{i} ")) + "|\n|" + string.Concat(Enumerable.Repeat("---|", 15)),
            "# Заголовок\u0001\n| a | b\u0007 |\n|---|---|\n| 1 | 2 |\n\n[битая ссылка](не url) и [якорь](#раздел)",
        };
        foreach (var format in AllFormats)
        {
            foreach (var markdown in samples)
            {
                var bytes = OfficeDocumentWriter.Create(format, markdown, "Отчёт");
                var errors = Validate(format, bytes);
                Assert(errors.Count == 0,
                    $"{format} for «{markdown[..Math.Min(30, markdown.Length)]}» has {errors.Count} validation errors:\n{string.Join("\n", errors.Take(15))}");
            }
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ docx

    private static Task DocxStructureAsync()
    {
        var bytes = OfficeDocumentWriter.Create(OfficeFormat.Docx, RichMarkdown, "Отчёт");
        using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = package.MainDocumentPart!;
        var body = main.Document.Body!;

        var styleIds = main.StyleDefinitionsPart!.Styles!.Elements<W.Style>().Select(s => s.StyleId?.Value).ToHashSet();
        foreach (var id in new[] { "Title", "Heading1", "Heading2", "Heading3", "ListParagraph", "Quote", "CodeBlock", "Hyperlink", "InlineCode" })
        {
            Assert(styleIds.Contains(id), $"style {id} must be defined");
        }

        var paragraphs = body.Elements<W.Paragraph>().ToList();
        string? StyleOf(W.Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        Assert(StyleOf(paragraphs[0]) == "Title" && paragraphs[0].InnerText == "Квартальный отчёт ConexyAI",
            "a single leading # heading is the document title");
        Assert(paragraphs.Any(p => StyleOf(p) == "Heading2" && p.InnerText.Contains("Ключевые показатели")), "## becomes Heading2");
        Assert(paragraphs.Any(p => StyleOf(p) == "Heading3" && p.InnerText == "Итоги"), "### becomes Heading3");

        // Inline formatting of the first paragraph.
        var runs = paragraphs[1].Descendants<W.Run>().ToList();
        Assert(runs.Any(r => r.InnerText == "сводка" && r.RunProperties?.Bold is not null), "**bold** is a bold run");
        Assert(runs.Any(r => r.InnerText == "курсив" && r.RunProperties?.Italic is not null), "*italic* is an italic run");
        Assert(runs.Any(r => r.InnerText == "старое" && r.RunProperties?.Strike is not null), "~~strike~~ is struck through");
        Assert(runs.Any(r => r.InnerText == "inline code" && r.RunProperties?.RunStyle?.Val?.Value == "InlineCode"), "`code` uses the code style");
        var link = paragraphs[1].Descendants<W.Hyperlink>().SingleOrDefault();
        Assert(link is not null && link.InnerText == "ссылка", "[text](url) is a real hyperlink");
        var target = main.HyperlinkRelationships.Single(r => r.Id == link!.Id).Uri;
        Assert(target.AbsoluteUri.StartsWith("https://example.com/", StringComparison.Ordinal), $"the hyperlink points to the URL, got {target}");
        Assert(body.InnerText.Contains("snake_case_name"), "underscores inside words are not emphasis");

        // Lists: real numbering with nesting.
        var listItems = paragraphs.Where(p => p.ParagraphProperties?.NumberingProperties is not null).ToList();
        var levels = listItems.Select(p => p.ParagraphProperties!.NumberingProperties!.NumberingLevelReference!.Val!.Value).Distinct().ToList();
        Assert(levels.Contains(0) && levels.Contains(1) && levels.Contains(2), $"nested bullets use levels 0-2, got [{string.Join(",", levels)}]");
        var numbering = main.NumberingDefinitionsPart!.Numbering!;
        var formats = numbering.Elements<W.AbstractNum>()
            .SelectMany(a => a.Elements<W.Level>())
            .Select(l => l.NumberingFormat!.Val!.Value)
            .ToHashSet();
        Assert(formats.Contains(W.NumberFormatValues.Bullet) && formats.Contains(W.NumberFormatValues.Decimal),
            "numbering defines both bullets and decimal numbers");
        var first = listItems.First(p => p.InnerText == "Первый шаг").ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        var second = listItems.First(p => p.InnerText == "Второй шаг").ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        var bullet = listItems.First(p => p.InnerText.StartsWith("Выручка")).ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value;
        Assert(first == second && first != bullet, "one ordered list shares one numbering instance, separate from bullets");
        Assert(listItems.Any(p => p.InnerText == "☑ Сделано") && listItems.Any(p => p.InnerText == "☐ Не сделано"), "task items keep their state");

        // Table: borders, repeated bold shaded header, escaped pipe stays in its cell.
        var table = body.Elements<W.Table>().Single();
        Assert(table.GetFirstChild<W.TableProperties>()!.TableBorders!.InsideHorizontalBorder is not null, "the table has borders");
        var header = table.Elements<W.TableRow>().First();
        Assert(header.TableRowProperties?.GetFirstChild<W.TableHeader>() is not null, "the header row repeats on every page");
        Assert(header.Elements<W.TableCell>().All(c => c.TableCellProperties?.Shading?.Fill?.Value == "D9E2F3"), "header cells are shaded");
        Assert(header.Descendants<W.Run>().All(r => r.RunProperties?.Bold is not null), "header text is bold");
        var lastRow = table.Elements<W.TableRow>().Last().Elements<W.TableCell>().ToList();
        Assert(lastRow.Count == 8 && lastRow[0].InnerText == "Итого | всё", "an escaped pipe stays inside its cell");
        var gridWidth = table.GetFirstChild<W.TableGrid>()!.Elements<W.GridColumn>().Sum(g => int.Parse(g.Width!.Value!));
        Assert(gridWidth == 9355, $"the columns fill the text width exactly, got {gridWidth}");

        // A table too wide for the page steps its font down instead of breaking every word.
        var wideMarkdown = "| " + string.Join(" | ", Enumerable.Range(1, 12).Select(i => $"Показатель{i}")) + " |\n|"
                           + string.Concat(Enumerable.Repeat("---|", 12)) + "\n| " + string.Join(" | ", Enumerable.Range(1, 12).Select(i => $"{i * 1000}")) + " |";
        using (var wide = WordprocessingDocument.Open(new MemoryStream(OfficeDocumentWriter.Create(OfficeFormat.Docx, wideMarkdown)), false))
        {
            var sizes = wide.MainDocumentPart!.Document.Body!.Descendants<W.Run>().Select(r => r.RunProperties?.FontSize?.Val?.Value).Distinct().ToList();
            Assert(sizes.Count == 1 && sizes[0] is "18" or "16", $"a 12-column table uses a smaller font, got [{string.Join(",", sizes)}]");
        }
        using (var narrow = WordprocessingDocument.Open(new MemoryStream(OfficeDocumentWriter.Create(OfficeFormat.Docx, "| a | b |\n|---|---|\n| 1 | 2 |")), false))
        {
            Assert(narrow.MainDocumentPart!.Document.Body!.Descendants<W.Run>().All(r => r.RunProperties?.FontSize is null),
                "a narrow table keeps the body font");
        }

        // Code block: monospace style, one paragraph with line breaks and a tab.
        var code = paragraphs.Single(p => StyleOf(p) == "CodeBlock");
        Assert(code.Descendants<W.Break>().Any() && code.Descendants<W.TabChar>().Any(), "code keeps its lines and tabs");
        var codeStyle = main.StyleDefinitionsPart.Styles.Elements<W.Style>().Single(s => s.StyleId == "CodeBlock");
        Assert(codeStyle.StyleRunProperties?.RunFonts?.Ascii?.Value == "Consolas", "code blocks are monospace");
        Assert(paragraphs.Any(p => StyleOf(p) == "Quote" && p.InnerText.Contains("важной")), "blockquotes use the Quote style");

        Assert(package.PackageProperties.Title == "Квартальный отчёт ConexyAI", $"title property, got {package.PackageProperties.Title}");
        Assert(package.PackageProperties.Creator == "ConexyAI", "creator property");

        // Two ordered lists separated by text restart; "3." after a paragraph keeps its number.
        var restart = OfficeDocumentWriter.Create(OfficeFormat.Docx, "1. a\n2. b\n\nтекст\n\n1. c\n2. d\n\nещё\n\n3. e");
        using var restarted = WordprocessingDocument.Open(new MemoryStream(restart), false);
        var items = restarted.MainDocumentPart!.Document.Body!.Elements<W.Paragraph>()
            .Where(p => p.ParagraphProperties?.NumberingProperties is not null)
            .ToDictionary(p => p.InnerText, p => p.ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value);
        Assert(items["a"] == items["b"] && items["c"] == items["d"] && items["a"] != items["c"], "separate ordered lists restart");
        var instance = restarted.MainDocumentPart.NumberingDefinitionsPart!.Numbering!.Elements<W.NumberingInstance>()
            .Single(n => n.NumberID!.Value == items["e"]);
        Assert(instance.Descendants<W.StartOverrideNumberingValue>().Single().Val!.Value == 3, "a list starting at 3 starts at 3");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ xlsx

    private static Task XlsxTypedCellsAsync()
    {
        var bytes = OfficeDocumentWriter.Create(OfficeFormat.Xlsx, RichMarkdown, "Отчёт");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Цены");

        Assert(sheet.Cell("A1").GetString() == "Кофейня" && sheet.Cell("A1").Style.Font.Bold, "the header is bold text");
        Assert(sheet.Cell("A1").Style.Fill.BackgroundColor.Color.ToArgb() != System.Drawing.Color.Transparent.ToArgb(), "the header is filled");
        Assert(sheet.Cell("B2").DataType == XLDataType.Number && sheet.Cell("B2").GetDouble() == 1290.5, "\"1 290,50\" is 1290.5");
        Assert(sheet.Cell("B2").Style.NumberFormat.Format == "#,##0.00", $"grouped decimals keep their look, got {sheet.Cell("B2").Style.NumberFormat.Format}");
        Assert(sheet.Cell("B4").GetDouble() == -1234.56, "negative grouped numbers");
        Assert(sheet.Cell("C2").GetDouble() == 0.12 && sheet.Cell("C2").Style.NumberFormat.Format.Contains('%'), "12% is a percentage");
        Assert(Math.Abs(sheet.Cell("C3").GetDouble() + 0.035) < 1e-12, "\"-3,5 %\" is -0.035");
        Assert(sheet.Cell("D2").DataType == XLDataType.DateTime && sheet.Cell("D2").GetDateTime() == new DateTime(2026, 9, 23), "ISO dates are dates");
        Assert(sheet.Cell("D3").GetDateTime() == new DateTime(2026, 9, 23) && sheet.Cell("D3").Style.DateFormat.Format == "dd.mm.yyyy", "dd.mm.yyyy dates are dates");
        Assert(sheet.Cell("E2").GetDouble() == 1200 && sheet.Cell("E2").Style.NumberFormat.Format.Contains('₽'), "\"1 200 ₽\" is money in rubles");
        Assert(sheet.Cell("E3").GetDouble() == 1200.5 && sheet.Cell("E3").Style.NumberFormat.Format.Contains('$'), "\"$1,200.50\" is money in dollars");
        Assert(sheet.Cell("E4").GetDouble() == 15, "\"€15\" is money");
        Assert(sheet.Cell("F2").DataType == XLDataType.Boolean && sheet.Cell("F2").GetBoolean(), "true is a boolean");
        Assert(sheet.Cell("F3").DataType == XLDataType.Boolean && !sheet.Cell("F3").GetBoolean(), "false is a boolean");
        Assert(sheet.Cell("G2").DataType == XLDataType.Text && sheet.Cell("G2").GetString() == "007", "leading zeros stay text");
        Assert(sheet.Cell("G4").DataType == XLDataType.Text, "phone numbers stay text");
        Assert(sheet.Cell("H2").HasFormula && sheet.Cell("H2").FormulaA1 == "B2*2", $"=B2*2 is a formula, got {sheet.Cell("H2").FormulaA1}");
        Assert(sheet.Cell("H3").HasFormula && sheet.Cell("H3").FormulaA1 == "SUM(B2:B3)", $"Russian СУММ becomes SUM, got {sheet.Cell("H3").FormulaA1}");
        Assert(!sheet.Cell("H4").HasFormula && sheet.Cell("H4").GetString() == "=== Итого ===", "text starting with = is not a formula");
        Assert(sheet.Cell("A4").GetString() == "Итого | всё", "an escaped pipe stays in its cell");

        Assert(sheet.SheetView.SplitRow == 1, "the header row is frozen");
        Assert(sheet.AutoFilter.IsEnabled && sheet.AutoFilter.Range.RangeAddress.ToString() == "A1:H4",
            $"an autofilter covers the table, got {sheet.AutoFilter.Range?.RangeAddress}");
        Assert(sheet.Cell("B3").Style.Border.TopBorder == XLBorderStyleValues.Thin, "cells have borders");
        for (var c = 1; c <= 8; c++)
        {
            var width = sheet.Column(c).Width;
            Assert(width is >= 8 and <= 60, $"column {c} width {width} is fitted within 8..60");
        }
        Assert(sheet.Column(7).Width > sheet.Column(3).Width, "a column with longer text is wider");
        Assert(workbook.Properties.Author == "ConexyAI", "author property");

        // Cached values let previews and the parser show formula results.
        var text = DocumentParser.Parse(bytes, "r.xlsx");
        Assert(text.Contains("| Бодрость | 1290.5 | 12% | 2026-09-23 |") && text.Contains("| 2581 |"),
            $"the parser shows dates, percentages and formula results, got: {text}");
        return Task.CompletedTask;
    }

    private static Task XlsxNumbersAndSheetNamesAsync()
    {
        // An English document: "12,500" is twelve thousand five hundred.
        var english = OfficeDocumentWriter.Create(OfficeFormat.Xlsx,
            "## Revenue\n| Item | Amount | Other |\n|---|---|---|\n| A | 12,500 | 1,234.56 |\n| B | (1 234) | −5 |\n| C | 1 234 | 1,5 |");
        using (var workbook = new XLWorkbook(new MemoryStream(english)))
        {
            var sheet = workbook.Worksheet("Revenue");
            Assert(sheet.Cell("B2").GetDouble() == 12500, $"12,500 in an English table is 12500, got {sheet.Cell("B2").Value}");
            Assert(sheet.Cell("C2").GetDouble() == 1234.56, "1,234.56 is 1234.56");
            Assert(sheet.Cell("B3").GetDouble() == -1234, "accounting negatives (1 234) are negative");
            Assert(sheet.Cell("C3").GetDouble() == -5, "a Unicode minus is a minus");
            Assert(sheet.Cell("B4").GetDouble() == 1234, "NBSP thousands separators");
        }

        // A Russian document: "12,500" is twelve and a half; identifiers stay text.
        var russian = OfficeDocumentWriter.Create(OfficeFormat.Xlsx,
            "## Вес\n| Товар | Кг | Цена | ИНН | Год | Код |\n|---|---|---|---|---|---|\n| Кофе | 12,500 | 1 200 руб. | 7707083893123 | 2026 | 89991234567 |");
        using (var workbook = new XLWorkbook(new MemoryStream(russian)))
        {
            var sheet = workbook.Worksheet("Вес");
            Assert(sheet.Cell("B2").GetDouble() == 12.5, $"12,500 in a Russian table is 12.5, got {sheet.Cell("B2").Value}");
            Assert(sheet.Cell("C2").GetDouble() == 1200 && sheet.Cell("C2").Style.NumberFormat.Format.Contains("руб."), "\"1 200 руб.\" is money");
            Assert(sheet.Cell("D2").DataType == XLDataType.Text, "long digit identifiers stay text");
            Assert(sheet.Cell("E2").GetDouble() == 2026 && sheet.Cell("E2").Style.NumberFormat.Format is "" or "General",
                $"a year is a plain number, not \"2 026\", got format {sheet.Cell("E2").Style.NumberFormat.Format}");
            Assert(sheet.Cell("F2").DataType == XLDataType.Text, "phone-like digit strings stay text");
        }

        // Sheet names: invalid characters stripped, ≤ 31 characters, unique, never "History".
        const string longTitle = "Очень длинное название раздела с финансовой моделью на три года";
        var names = OfficeDocumentWriter.Create(OfficeFormat.Xlsx,
            $"## Итоги: Q1/Q2 [черновик]*?\n| a |\n|---|\n| 1 |\n\n## {longTitle}\n| a |\n|---|\n| 1 |\n\n## {longTitle}\n| a |\n|---|\n| 1 |\n\n## History\n| a |\n|---|\n| 1 |\n\n| без заголовка |\n|---|\n| 1 |");
        using (var workbook = new XLWorkbook(new MemoryStream(names)))
        {
            var sheetNames = workbook.Worksheets.Select(s => s.Name).ToList();
            Assert(sheetNames.Count == 5, $"one sheet per table, got [{string.Join(", ", sheetNames)}]");
            Assert(sheetNames.All(n => n.Length is > 0 and <= 31 && n.IndexOfAny("[]:*?/\\".ToCharArray()) < 0), $"valid names, got [{string.Join(", ", sheetNames)}]");
            Assert(sheetNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == sheetNames.Count, "unique names");
            Assert(sheetNames[0] == "Итоги Q1Q2 черновик", $"invalid characters are stripped, got {sheetNames[0]}");
            Assert(!sheetNames.Contains("History"), "\"History\" is reserved by Excel");
        }

        // No tables: one sheet of text lines, headings bold.
        var prose = OfficeDocumentWriter.Create(OfficeFormat.Xlsx, "# План\nОткрыть кофейню.\n- аренда\n- персонал", "План");
        using (var workbook = new XLWorkbook(new MemoryStream(prose)))
        {
            var sheet = workbook.Worksheets.Single();
            Assert(sheet.Cell("A1").GetString() == "План" && sheet.Cell("A1").Style.Font.Bold, "headings are bold lines");
            Assert(sheet.Cell("A2").GetString() == "Открыть кофейню." && sheet.Cell("A3").GetString() == "• аренда", "text and list items follow line by line");
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ pptx

    private static Task PptxStructureAsync()
    {
        var bytes = OfficeDocumentWriter.Create(OfficeFormat.Pptx, RichMarkdown, "Deck");
        using var package = PresentationDocument.Open(new MemoryStream(bytes), false);
        var presentation = package.PresentationPart!;
        Assert(presentation.SlideMasterParts.Count() == 1 && presentation.SlideMasterParts.Single().SlideLayoutParts.Count() == 3,
            "one master with its layouts");
        Assert(presentation.ThemePart is not null && presentation.SlideMasterParts.Single().ThemePart is not null, "a theme for the master");

        var slides = presentation.Presentation.SlideIdList!.Elements<P.SlideId>()
            .Select(id => (SlidePart)presentation.GetPartById(id.RelationshipId!))
            .ToList();
        Assert(slides.All(s => s.SlideLayoutPart is not null), "every slide has a layout");

        static string? Placeholder(SlidePart slide, params P.PlaceholderValues[] types) =>
            slide.Slide.Descendants<P.Shape>()
                .FirstOrDefault(s => s.Descendants<P.PlaceholderShape>().Any(p => p.Type?.Value is { } t && types.Contains(t)))
                ?.TextBody?.InnerText;

        Assert(Placeholder(slides[0], P.PlaceholderValues.CenteredTitle) == "Квартальный отчёт ConexyAI", "the first # heading is the title slide");
        Assert(Placeholder(slides[0], P.PlaceholderValues.SubTitle)?.Contains("сводка") == true, "the paragraph under it is the subtitle");

        var titles = slides.Skip(1).Select(s => Placeholder(s, P.PlaceholderValues.Title, P.PlaceholderValues.CenteredTitle)).ToList();
        Assert(titles.Contains("Ключевые показатели"), $"\"## Слайд 2: X\" titles the slide X, got [{string.Join(" / ", titles)}]");
        Assert(titles.Contains("English slide"), "\"## Slide 5: X\" titles the slide X");
        Assert(titles.Contains("Цены"), "every ## heading starts a slide");

        var levels = slides.SelectMany(s => s.Slide.Descendants<A.ParagraphProperties>())
            .Where(p => p.Level is not null)
            .Select(p => p.Level!.Value)
            .ToHashSet();
        Assert(levels.Contains(0) && levels.Contains(1) && levels.Contains(2), $"nested bullets keep their levels, got [{string.Join(",", levels)}]");
        Assert(slides.Any(s => s.Slide.Descendants<A.AutoNumberedBullet>().Any()), "numbered items are numbered");

        var tableSlide = slides.Single(s => s.Slide.Descendants<A.Table>().Any());
        var table = tableSlide.Slide.Descendants<A.Table>().Single();
        Assert(table.TableGrid!.Elements<A.GridColumn>().Count() == 8, "the table keeps its 8 columns");
        Assert(table.Elements<A.TableRow>().All(r => r.Elements<A.TableCell>().Count() == 8), "every row has every cell");
        var headerFill = table.Elements<A.TableRow>().First().Elements<A.TableCell>().First().TableCellProperties!.GetFirstChild<A.SolidFill>();
        Assert(headerFill?.RgbColorModelHex?.Val?.Value == "D9E2F3", "the header row is shaded");

        // Long tables continue on further slides with the header repeated; long lists continue too.
        var longTable = OfficeDocumentWriter.Create(OfficeFormat.Pptx,
            "## Реестр\n| № | Название |\n|---|---|\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"| {i} | Позиция {i} |")));
        using (var deck = PresentationDocument.Open(new MemoryStream(longTable), false))
        {
            var tables = deck.PresentationPart!.SlideParts.SelectMany(s => s.Slide.Descendants<A.Table>()).ToList();
            Assert(tables.Count > 1, "a long table is split across slides");
            Assert(tables.All(t => t.Elements<A.TableRow>().First().InnerText.StartsWith("№")), "each part repeats the header");
            var text = DocumentParser.Parse(longTable, "t.pptx");
            Assert(text.Contains("| 40 | Позиция 40 |") && text.Contains("Реестр (продолжение)"), "no row is lost");
        }

        // "1. 1. 1." numbering reads 1, 2, 3 and continues on the next slide instead of restarting.
        var lazyList = OfficeDocumentWriter.Create(OfficeFormat.Pptx, "## Шаги\n" + string.Join("\n", Enumerable.Range(1, 12).Select(i => $"1. шаг {i}")));
        using (var deck = PresentationDocument.Open(new MemoryStream(lazyList), false))
        {
            var starts = deck.PresentationPart!.Presentation.SlideIdList!.Elements<P.SlideId>()
                .Select(id => (SlidePart)deck.PresentationPart.GetPartById(id.RelationshipId!))
                .Select(s => s.Slide.Descendants<A.AutoNumberedBullet>().FirstOrDefault()?.StartAt?.Value)
                .Where(v => v is not null)
                .ToList();
            Assert(starts.Count == 2 && starts[0] == 1 && starts[1] > 1, $"the continued list keeps counting, got [{string.Join(",", starts)}]");
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ parser limits (H4)

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] Workbook(string sheetData) => Zip(
        ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Bomb\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
        ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{sheetData}</sheetData></worksheet>"));

    private static Task XlsxColumnBombAsync()
    {
        // The review's reproduction: 1000 rows, each with one cell in column XFD. It used to become
        // 49 million characters (every row padded with 16 383 empty cells).
        var rows = new StringBuilder("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>начало</t></is></c></row>");
        for (var r = 2; r <= 1001; r++)
        {
            rows.Append($"<row r=\"{r}\"><c r=\"XFD{r}\"><v>1</v></c></row>");
        }
        var bomb = Workbook(rows.ToString());
        Assert(bomb.Length < 64 * 1024, $"the bomb itself is small ({bomb.Length} bytes)");

        var watch = Stopwatch.StartNew();
        var text = DocumentParser.Parse(bomb, "bomb.xlsx");
        watch.Stop();
        Assert(text.Length < 1_000, $"output stays bounded, got {text.Length} characters");
        Assert(text.Contains("| начало |"), "the real cell is still read");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(5), $"parsing is fast, took {watch.Elapsed}");

        // Junk references are skipped without overflow; a sparse row is not padded past its last cell.
        var junk = Workbook(
            "<row r=\"1\"><c r=\"ZZZZZZZZZZZZZZZZ1\"><v>9</v></c><c r=\"ЯЯ1\"><v>8</v></c><c r=\"Ａ1\"><v>7</v></c><c r=\"B1\"><v>5</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\"><v>1</v></c><c r=\"IV2\"><v>2</v></c><c r=\"XFD2\"><v>3</v></c></row>" +
            "<row r=\"3\"><c><v>10</v></c><c><v>11</v></c></row>");
        var parsed = DocumentParser.Parse(junk, "junk.xlsx");
        var lines = parsed.Split('\n');
        Assert(lines.Contains("|  | 5 |"), $"only the valid reference of row 1 is read, got: {parsed}");
        var row2 = lines.Single(l => l.StartsWith("| 1 |"));
        Assert(row2.Count(c => c == '|') == DocumentParser.MaxColumns + 1 && row2.EndsWith("| 2 |"),
            "a row ends at its last cell within the column cap; XFD is beyond it");
        Assert(lines.Contains("| 10 | 11 |"), "cells without a reference follow each other");

        // The same reference repeated in one row overwrites, it does not pile up (kept under 1 MB so the
        // compression-ratio check does not refuse the sheet first).
        var repeated = Workbook("<row r=\"1\">" + string.Concat(Enumerable.Repeat("<c r=\"A1\" t=\"inlineStr\"><is><t>x</t></is></c>", 20_000)) + "</row>");
        var once = DocumentParser.Parse(repeated, "repeated.xlsx");
        Assert(once.Split('\n').Contains("| x |") && once.Length < 100, $"a repeated reference yields one cell, got {once.Length} characters");

        // Row cap per sheet.
        var many = Workbook(string.Concat(Enumerable.Range(1, DocumentParser.MaxRowsPerSheet + 50).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\"><v>1</v></c></row>")));
        var capped = DocumentParser.Parse(many, "many.xlsx");
        Assert(capped.Split('\n').Count(l => l == "| 1 |") == DocumentParser.MaxRowsPerSheet && capped.Contains("пропущены"),
            "rows beyond the cap are dropped with a note");
        return Task.CompletedTask;
    }

    private static Task ZipLimitsAsync()
    {
        // A classic bomb: 30 MB of one repeated paragraph, compressed more than 100:1.
        var paragraph = "<w:p><w:r><w:t>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</w:t></w:r></w:p>";
        var huge = new StringBuilder(31 * 1024 * 1024);
        huge.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        while (huge.Length < 30 * 1024 * 1024) huge.Append(paragraph);
        huge.Append("</w:body></w:document>");
        var bomb = Zip(("word/document.xml", huge.ToString()));
        Assert(bomb.Length * 100 < 30 * 1024 * 1024, $"the test archive is a real bomb ({bomb.Length} bytes)");

        var watch = Stopwatch.StartNew();
        var text = DocumentParser.Parse(bomb, "bomb.docx");
        Assert(text.Length == 0, $"a bomb-like entry is not inflated, got {text.Length} characters");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(5), $"refused quickly, took {watch.Elapsed}");

        // Too many entries: refused before .NET reads the central directory.
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var i = 0; i <= DocumentParser.MaxZipEntries; i++) zip.CreateEntry($"f{i}");
                using var document = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
                document.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>x</w:t></w:r></w:p></w:body></w:document>");
            }
            Assert(DocumentParser.Parse(buffer.ToArray(), "flood.docx").Length == 0, "an archive with too many entries is refused");
        }

        // A plain, large document is cut at the character budget with a note, not refused.
        var words = string.Concat(Enumerable.Range(0, 12_000).Select(i => $"<w:p><w:r><w:t>Абзац номер {i} с текстом для проверки бюджета.</w:t></w:r></w:p>"));
        var large = Zip(("word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{words}</w:body></w:document>"));
        var cut = DocumentParser.Parse(large, "large.docx");
        Assert(cut.Length <= DocumentParser.MaxOutputChars + 200 && cut.Contains("обрезан"), $"large text is cut with a note, got {cut.Length}");
        Assert(cut.StartsWith("Абзац номер 0 "), "the beginning is kept");

        // Garbage and truncated archives never throw.
        Assert(DocumentParser.Parse(new byte[] { 0x50, 0x4B, 3, 4, 1, 2, 3 }, "broken.xlsx").Length == 0, "a broken zip yields no text");
        Assert(DocumentParser.Parse(bomb[..(bomb.Length / 2)], "half.pptx").Length == 0, "a truncated zip yields no text");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ text decoding (M6)

    private static Task TextDecodingAsync()
    {
        const string text = "Привет, мир! Hello 123";
        byte[] With(byte[] bom, Encoding encoding) => bom.Concat(encoding.GetBytes(text)).ToArray();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var cases = new (string Name, byte[] Bytes)[]
        {
            ("UTF-8", Encoding.UTF8.GetBytes(text)),
            ("UTF-8 BOM", With(new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(false))),
            ("UTF-16 LE BOM", With(new byte[] { 0xFF, 0xFE }, new UnicodeEncoding(false, false))),
            ("UTF-16 BE BOM", With(new byte[] { 0xFE, 0xFF }, new UnicodeEncoding(true, false))),
            ("UTF-32 LE BOM", With(new byte[] { 0xFF, 0xFE, 0, 0 }, new UTF32Encoding(false, false))),
            ("UTF-32 BE BOM", With(new byte[] { 0, 0, 0xFE, 0xFF }, new UTF32Encoding(true, false))),
            ("UTF-16 LE", new UnicodeEncoding(false, false).GetBytes(text)),
            ("UTF-16 BE", new UnicodeEncoding(true, false).GetBytes(text)),
            ("Windows-1251", Encoding.GetEncoding(1251).GetBytes(text)),
        };
        foreach (var (name, bytes) in cases)
        {
            var decoded = DocumentParser.Parse(bytes, "note.txt");
            Assert(decoded == text, $"{name} must decode to the original text, got «{decoded}»");
        }

        var dirty = DocumentParser.Parse(Encoding.UTF8.GetBytes("a\0b\u0001c\u0007d\te\u007Ff\u0085g"), "dirty.csv");
        Assert(dirty == "abcd\tefg", $"NUL and other control characters are stripped, got «{dirty}»");
        Assert(TextSanitizer.Clean("ok") is var same && ReferenceEquals(same, "ok"), "clean text is returned as is");
        Assert(TextSanitizer.Clean("a\uD800b") == "ab", "lone surrogates are stripped");
        Assert(TextSanitizer.Clean("эмодзи 😀") == "эмодзи 😀", "surrogate pairs survive");

        // The attachment path: a UTF-16 file must reach the model as text, without NUL.
        var attachment = new TaskAttachment("заметки.txt", Convert.ToBase64String(With(new byte[] { 0xFF, 0xFE }, Encoding.Unicode)), "text/plain");
        var message = AttachmentText.ComposeUserMessage("Что тут?\0", new List<TaskAttachment> { attachment });
        Assert(message.Contains(text) && !message.Contains('\0'), $"the attachment is decoded and NUL-free, got: {message}");
        return Task.CompletedTask;
    }

    private static Task SurrogateSafetyAsync()
    {
        static bool HasLoneSurrogate(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                if (char.IsSurrogate(s[i])) return true;
            }
            return false;
        }

        // "a" shifts the pairs so that the attachment limit falls between the halves of an emoji.
        var emoji = "a" + string.Concat(Enumerable.Repeat("😀", AttachmentText.MaxCharsPerAttachment));
        var attachment = new TaskAttachment("e.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes(emoji)), "text/plain");
        var message = AttachmentText.ComposeUserMessage("?", new List<TaskAttachment> { attachment });
        Assert(message.Contains("показаны первые") && !HasLoneSurrogate(message), "truncating an attachment keeps surrogate pairs whole");

        foreach (var size in new[] { 7, 800, 801 })
        {
            var chunks = DocumentChunker.Chunk(emoji[..5_001], size, size / 7);
            Assert(chunks.Count > 1 && chunks.All(c => !HasLoneSurrogate(c)), $"chunks of {size} never split a surrogate pair");
        }
        // Every word ends with "!", so a chunk cut inside a word would end without one.
        var words = string.Join(' ', Enumerable.Range(0, 2_000).Select(i => $"слово{i}!"));
        var wordChunks = DocumentChunker.Chunk(words, 800, 120);
        Assert(wordChunks.All(c => c.Length <= 800 && c.EndsWith('!')), "chunks end at word boundaries");
        Assert(wordChunks.Last().EndsWith("слово1999!"), "the last word is in the last chunk");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ RAG documents (L6, M9)

    private static (DbConexy Context, DocumentService Service, string Directory) CreateDocumentService(IDocumentRepository? repository = null)
    {
        var context = new DbConexy(new DbContextOptionsBuilder<DbConexy>().UseInMemoryDatabase("docs_" + Guid.NewGuid().ToString("N")).Options);
        var directory = Path.Combine(Path.GetTempPath(), "conexy_docs_" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new RagOptions { DocumentsDirectory = directory, ChunkSize = 800, ChunkOverlap = 120 });
        return (context, new DocumentService(repository ?? new DocumentRepository(context), options), directory);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task ReadChunkPagesAsync()
    {
        var (context, service, directory) = CreateDocumentService();
        try
        {
            var userId = Guid.NewGuid();
            var text = string.Join("\n", Enumerable.Range(0, 3_000).Select(i => $"Строка {i}: договор аренды, пункт {i}."));
            var document = await service.IndexAsync(Encoding.UTF8.GetBytes(text), "договор.txt", "text/plain", userId);
            Assert(document.Chunks.Count > 50, $"the document has many chunks ({document.Chunks.Count})");

            var pages = new List<DocumentChunkResult>();
            int? next = null;
            do
            {
                var json = await service.ReadChunkJsonAsync(userId, document.Id, next);
                var page = JsonSerializer.Deserialize<DocumentChunkResult>(json)!;
                Assert(page.Content.Length <= DocumentService.ReadPageChars, $"a page is at most {DocumentService.ReadPageChars} characters, got {page.Content.Length}");
                Assert(page.TotalChunks == document.Chunks.Count, "the total is reported");
                if (page.NextChunkIndex is { } n)
                {
                    Assert(n == page.LastChunkIndex + 1 && page.Note!.Contains($"chunk_index = {n}"), "the note says how to continue");
                }
                pages.Add(page);
                next = page.NextChunkIndex;
            }
            while (next is not null && pages.Count < 100);

            Assert(pages.Count > 1 && pages[0].ChunkIndex == 0, "without an index the first page is returned, not the whole document");
            Assert(pages.Last().LastChunkIndex == document.Chunks.Count - 1 && pages.Last().Note is null, "paging reaches the last chunk");
            var joined = string.Join("\n", pages.Select(p => p.Content));
            Assert(joined.Contains("Строка 0:") && joined.Contains("Строка 2999:"), "paging covers the document");
            Assert(joined.Length < text.Length * 1.05, $"overlaps are not repeated ({joined.Length} vs {text.Length})");

            var missing = await service.ReadChunkJsonAsync(userId, document.Id, 100_000);
            Assert(missing.Contains("not found"), "an index past the end is an error");
            Assert((await service.ReadChunkJsonAsync(Guid.NewGuid(), document.Id, null)).Contains("Document not found"), "another user's document is not readable");
        }
        finally
        {
            await context.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    private static DocumentController Controller(IDocumentService service, IDocumentRepository repository, Guid userId) => new(service, repository)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test")),
            },
        },
    };

    private static async Task DeleteDocumentAsync()
    {
        var (context, service, directory) = CreateDocumentService();
        try
        {
            var owner = Guid.NewGuid();
            var stranger = Guid.NewGuid();
            var repository = new DocumentRepository(context);
            var document = await service.IndexAsync(Encoding.UTF8.GetBytes(string.Join(' ', Enumerable.Repeat("текст", 1_000))), "a.txt", "text/plain", owner);
            var kept = await service.IndexAsync(Encoding.UTF8.GetBytes("другой документ"), "b.txt", "text/plain", owner);
            Assert(File.Exists(document.FilePath) && document.Chunks.Count > 1, "the upload is stored and chunked");

            var foreign = await Controller(service, repository, stranger).Delete(document.Id, CancellationToken.None);
            Assert(foreign is NotFoundObjectResult, "another user's document is a 404");
            Assert(await context.Documents.AnyAsync(d => d.Id == document.Id) && File.Exists(document.FilePath), "and stays intact");
            Assert(await Controller(service, repository, owner).Delete(Guid.NewGuid(), CancellationToken.None) is NotFoundObjectResult, "an unknown id is a 404");

            var deleted = await Controller(service, repository, owner).Delete(document.Id, CancellationToken.None);
            Assert(deleted is NoContentResult, "the owner's delete is a 204");
            Assert(!await context.Documents.AnyAsync(d => d.Id == document.Id), "the row is gone");
            Assert(!await context.DocumentChunks.AnyAsync(c => c.DocumentId == document.Id), "the chunks are gone");
            Assert(!File.Exists(document.FilePath), "the stored file is gone");
            Assert(await context.Documents.AnyAsync(d => d.Id == kept.Id) && File.Exists(kept.FilePath), "other documents are untouched");

            Assert(await service.DeleteAllForUserAsync(owner) == 1 && !File.Exists(kept.FilePath), "deleting all of a user's documents removes their files too");
        }
        finally
        {
            await context.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    /// <summary>A repository whose save fails, as when Postgres rejects a row.</summary>
    private sealed class FailingRepository : IDocumentRepository
    {
        public Task<Document> AddDocumentAsync(Document document, CancellationToken ct = default) =>
            throw new InvalidOperationException("22021: invalid byte sequence for encoding \"UTF8\": 0x00");

        public Task<Document?> GetDocumentAsync(Guid userId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Document>> ListDocumentsAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSearchRow>> SearchAsync(Guid userId, string query, int limit, string? documentName, string textSearchConfig, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DocumentChunk?> GetChunkAsync(Guid userId, Guid documentId, int? chunkIndex, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(Guid userId, Guid documentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountChunksAsync(Guid userId, Guid documentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentChunk>> GetChunkRangeAsync(Guid userId, Guid documentId, int fromIndex, int take, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Document?> DeleteDocumentAsync(Guid userId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Document>> DeleteUserDocumentsAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task UploadFailureLeavesNoFileAsync()
    {
        var (context, service, directory) = CreateDocumentService(new FailingRepository());
        try
        {
            var failed = false;
            try
            {
                await service.IndexAsync(Encoding.UTF8.GetBytes("текст"), "a.txt", "text/plain", Guid.NewGuid());
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            Assert(failed, "the save error still reaches the caller");
            Assert(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0, "no orphaned file is left on disk");

            // The real repository: a UTF-16 file with NUL in its name is stored clean.
            var (realContext, realService, realDirectory) = CreateDocumentService();
            try
            {
                var userId = Guid.NewGuid();
                var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Регламент\0 отпусков")).ToArray();
                var controller = Controller(realService, new DocumentRepository(realContext), userId);
                var upload = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "регла\0мент.txt")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "text/plain",
                };
                var result = await controller.Upload(upload, CancellationToken.None);
                Assert(result.Result is OkObjectResult, "a UTF-16 upload is indexed");
                var stored = await realContext.Documents.Include(d => d.Chunks).SingleAsync();
                Assert(!stored.FileName.Contains('\0') && stored.Chunks.All(c => !c.Content.Contains('\0')), "names and chunks carry no NUL");
                Assert(stored.Chunks.Single().Content == "Регламент отпусков", $"the text is decoded, got «{stored.Chunks.Single().Content}»");

                var image = new FormFile(new MemoryStream(new byte[] { 1, 2, 3 }), 0, 3, "file", "photo.png") { Headers = new HeaderDictionary() };
                var rejected = await controller.Upload(image, CancellationToken.None);
                Assert(rejected.Result is BadRequestObjectResult, "a format without text is rejected");
                Assert(Directory.GetFiles(realDirectory).Length == 1, "and nothing is written for it");
            }
            finally
            {
                await realContext.DisposeAsync();
                DeleteDirectory(realDirectory);
            }
        }
        finally
        {
            await context.DisposeAsync();
            DeleteDirectory(directory);
        }
    }
}

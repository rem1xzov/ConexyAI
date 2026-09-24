using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using Ap = DocumentFormat.OpenXml.ExtendedProperties;
using P = DocumentFormat.OpenXml.Presentation;

namespace ConexyAI.Service.Office;

// OFFICE_OPENXML: добавлено 2026-09-24 — .pptx через DocumentFormat.OpenXml: полный пакет (мастер,
// три макета — титульный, «заголовок и объект», «только заголовок» — и тема), титульный слайд из
// первого «#» или названия документа, по слайду на каждый «#»/«##» (в т.ч. «## Слайд N: Название» —
// заголовком становится часть после двоеточия), маркированные и нумерованные пункты с уровнями,
// таблицы — настоящие таблицы PowerPoint, переполненные слайды продолжаются на следующем.
internal static partial class PptxWriter
{
    private const long SlideWidth = 12_192_000;
    private const long SlideHeight = 6_858_000;
    private const long MarginX = 838_200;
    private const long ContentWidth = SlideWidth - 2 * MarginX;
    private const long TitleTop = 365_125;
    private const long TitleHeight = 1_325_563;
    private const long BodyTop = 1_825_625;
    private const long BodyHeight = 4_351_338;
    private const long EmuPerPoint = 12_700;

    // Beyond this many (wrapped) lines a slide overflows at the body sizes below; the rest continues
    // on the next slide instead of running off the bottom.
    private const double MaxLinesPerSlide = 8;
    private const int MaxTableColumns = 12;
    private const string Continued = " (продолжение)";

    private enum LineKind
    {
        Bullet,
        Numbered,
        Plain,
        Heading,
        Code,
        Quote,
    }

    private sealed record SlideLine(IReadOnlyList<MdRun> Runs, int Depth, LineKind Kind, int Number = 0);

    private abstract record SlideItem;

    private sealed record LineItem(SlideLine Line) : SlideItem;

    private sealed record TableItem(MdTable Table) : SlideItem;

    private sealed record Section(string Title, List<SlideItem> Items);

    private abstract record SlideSpec(string Title);

    private sealed record TitleSlide(string Title, IReadOnlyList<IReadOnlyList<MdRun>> Subtitle) : SlideSpec(Title);

    private sealed record ContentSlide(string Title, IReadOnlyList<SlideLine> Lines) : SlideSpec(Title);

    private sealed record TableSlide(string Title, MdTable Table, IReadOnlyList<int> Rows) : SlideSpec(Title);

    public static byte[] Write(IReadOnlyList<MdBlock> blocks, string fallbackTitle)
    {
        var slides = Plan(blocks, fallbackTitle);

        using var stream = new MemoryStream();
        using (var package = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = package.AddPresentationPart();
            var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
            var themePart = presentationPart.AddNewPart<ThemePart>();
            themePart.Theme = Declare(Theme());
            masterPart.AddPart(themePart);

            var titleLayout = AddLayout(masterPart, TitleSlideLayout());
            var contentLayout = AddLayout(masterPart, ContentLayout());
            var titleOnlyLayout = AddLayout(masterPart, TitleOnlyLayout());

            masterPart.SlideMaster = Declare(SlideMaster(new[]
            {
                masterPart.GetIdOfPart(titleLayout),
                masterPart.GetIdOfPart(contentLayout),
                masterPart.GetIdOfPart(titleOnlyLayout),
            }));

            var slideIds = new P.SlideIdList();
            uint nextSlideId = 256;
            foreach (var spec in slides)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                var builder = new SlideBuilder(slidePart);
                switch (spec)
                {
                    case TitleSlide t:
                        slidePart.AddPart(titleLayout);
                        slidePart.Slide = builder.TitleSlide(t);
                        break;
                    case TableSlide t:
                        slidePart.AddPart(titleOnlyLayout);
                        slidePart.Slide = builder.TableSlide(t);
                        break;
                    case ContentSlide c:
                        slidePart.AddPart(contentLayout);
                        slidePart.Slide = builder.ContentSlide(c);
                        break;
                }
                slideIds.Append(new P.SlideId { Id = nextSlideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation = Declare(new P.Presentation(
                new P.SlideMasterIdList(new P.SlideMasterId { Id = 2_147_483_648U, RelationshipId = presentationPart.GetIdOfPart(masterPart) }),
                slideIds,
                new P.SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight },
                new P.NotesSize { Cx = 6_858_000, Cy = 9_144_000 },
                DefaultTextStyle())
            {
                SaveSubsetFonts = true,
            });

            presentationPart.AddNewPart<PresentationPropertiesPart>().PresentationProperties = new P.PresentationProperties();
            presentationPart.AddNewPart<ViewPropertiesPart>().ViewProperties = new P.ViewProperties(
                new P.NormalViewProperties(
                    new P.RestoredLeft { Size = 15_620 },
                    new P.RestoredTop { Size = 94_660 }),
                new P.GridSpacing { Cx = 76_200, Cy = 76_200 });
            presentationPart.AddNewPart<TableStylesPart>().TableStyleList =
                new A.TableStyleList { Default = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}" };

            var title = slides[0].Title;
            package.PackageProperties.Title = title;
            package.PackageProperties.Creator = OfficeDocumentWriter.Creator;
            package.PackageProperties.LastModifiedBy = OfficeDocumentWriter.Creator;
            package.PackageProperties.Created = DateTime.UtcNow;
            package.PackageProperties.Modified = DateTime.UtcNow;
            package.AddExtendedFilePropertiesPart().Properties = new Ap.Properties(
                new Ap.Application(OfficeDocumentWriter.Creator),
                new Ap.PresentationFormat("Widescreen"),
                new Ap.Slides(slides.Count.ToString()));
        }
        return stream.ToArray();
    }

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// The number each ordered item displays, as a Markdown renderer counts it ("1. 1. 1." reads 1, 2, 3),
    /// so a list that continues on the next slide continues its numbering.
    /// </summary>
    private static List<MdBlock> NumberLists(IReadOnlyList<MdBlock> blocks)
    {
        var next = new int?[9];
        var result = new List<MdBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            if (block is not MdListItem item)
            {
                Array.Clear(next);
                result.Add(block);
                continue;
            }

            var depth = Math.Clamp(item.Depth, 0, 8);
            for (var deeper = depth + 1; deeper < 9; deeper++) next[deeper] = null;
            if (!item.Ordered)
            {
                next[depth] = null;
                result.Add(item);
                continue;
            }

            var number = next[depth] ?? item.Number;
            next[depth] = number + 1;
            result.Add(item with { Number = number });
        }
        return result;
    }

    private static List<SlideSpec> Plan(IReadOnlyList<MdBlock> source, string fallbackTitle)
    {
        var blocks = NumberLists(source);
        var index = 0;
        string deckTitle;
        if (blocks.Count > 0 && blocks[0] is MdHeading { Level: 1 } lead)
        {
            deckTitle = SlideTitle(lead.Runs);
            index = 1;
        }
        else
        {
            deckTitle = fallbackTitle;
        }

        // Short paragraphs right under the deck title become its subtitle.
        var subtitle = new List<IReadOnlyList<MdRun>>();
        var subtitleLength = 0;
        while (index < blocks.Count && blocks[index] is MdParagraph p && subtitle.Count < 3)
        {
            var length = MarkdownBlocks.PlainText(p.Runs).Length;
            if (subtitleLength + length > 300) break;
            subtitle.Add(p.Runs);
            subtitleLength += length;
            index++;
        }

        var sections = new List<Section>();
        Section? current = null;
        for (; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block is MdHeading { Level: <= 2 } heading)
            {
                current = new Section(SlideTitle(heading.Runs), new List<SlideItem>());
                sections.Add(current);
                continue;
            }
            if (current is null)
            {
                current = new Section(deckTitle, new List<SlideItem>());
                sections.Add(current);
            }
            AddItems(current.Items, block, quote: false);
        }

        var slides = new List<SlideSpec> { new TitleSlide(deckTitle, subtitle) };
        foreach (var section in sections) Paginate(section, slides);
        return slides;
    }

    private static void AddItems(List<SlideItem> items, MdBlock block, bool quote)
    {
        switch (block)
        {
            case MdHeading h:
                items.Add(new LineItem(new SlideLine(h.Runs, 0, LineKind.Heading)));
                break;
            case MdParagraph p:
                items.Add(new LineItem(new SlideLine(p.Runs, 0, quote ? LineKind.Quote : LineKind.Plain)));
                break;
            case MdListItem li:
                var runs = li.Checked is { } done ? li.Runs.Prepend(new MdRun(done ? "☑ " : "☐ ")).ToList() : li.Runs;
                var kind = li.Ordered && li.Checked is null ? LineKind.Numbered : LineKind.Bullet;
                items.Add(new LineItem(new SlideLine(runs, Math.Min(li.Depth, 4), kind, li.Number)));
                break;
            case MdCode c:
                foreach (var line in c.Text.TrimEnd().Split('\n'))
                {
                    items.Add(new LineItem(new SlideLine(new[] { new MdRun(line.Length == 0 ? " " : line) }, 0, LineKind.Code)));
                }
                break;
            case MdQuote q:
                foreach (var inner in q.Blocks) AddItems(items, inner, quote: true);
                break;
            case MdTable t when t.ColumnCount <= MaxTableColumns:
                items.Add(new TableItem(t));
                break;
            case MdTable t:
                // Too wide for a slide: one line per row.
                for (var r = 0; r < t.Rows.Count; r++)
                {
                    var text = string.Join("  |  ", t.Rows[r]);
                    items.Add(new LineItem(new SlideLine(new[] { new MdRun(text, Bold: r == 0) }, 0, LineKind.Plain)));
                }
                break;
        }
    }

    /// <summary>A section becomes one or more slides: text pages by estimated height, tables on their own.</summary>
    private static void Paginate(Section section, List<SlideSpec> slides)
    {
        var first = true;
        string NextTitle()
        {
            var title = first ? section.Title : section.Title + Continued;
            first = false;
            return title;
        }

        if (section.Items.Count == 0)
        {
            // A heading with nothing under it ("## Спасибо!") is a section divider.
            slides.Add(new TitleSlide(section.Title, Array.Empty<IReadOnlyList<MdRun>>()));
            return;
        }

        var page = new List<SlideLine>();
        var weight = 0.0;
        foreach (var item in section.Items)
        {
            if (item is TableItem table)
            {
                if (page.Count > 0)
                {
                    slides.Add(new ContentSlide(NextTitle(), page));
                    page = new List<SlideLine>();
                    weight = 0;
                }
                foreach (var rows in TablePages(table.Table))
                {
                    slides.Add(new TableSlide(NextTitle(), table.Table, rows));
                }
                continue;
            }

            var line = ((LineItem)item).Line;
            var lineWeight = LineWeight(line);
            if (page.Count > 0 && weight + lineWeight > MaxLinesPerSlide)
            {
                slides.Add(new ContentSlide(NextTitle(), page));
                page = new List<SlideLine>();
                weight = 0;
            }
            page.Add(line);
            weight += lineWeight;
        }
        if (page.Count > 0) slides.Add(new ContentSlide(NextTitle(), page));
    }

    private static double LineWeight(SlideLine line)
    {
        var length = MarkdownBlocks.PlainText(line.Runs).Length;
        var (charsPerLine, height) = line.Kind switch
        {
            LineKind.Code => (110, 0.6),
            LineKind.Heading => (60, 1.0),
            _ when line.Depth == 0 => (70, 1.0),
            _ when line.Depth == 1 => (78, 0.9),
            _ => (86, 0.8),
        };
        var wrapped = Math.Max(1, (int)Math.Ceiling(length / (double)charsPerLine));
        return wrapped * height;
    }

    /// <summary>"## Слайд 3: Итоги" → "Итоги"; anything else stays as written.</summary>
    private static string SlideTitle(IReadOnlyList<MdRun> runs)
    {
        var text = MarkdownBlocks.PlainText(runs).Trim();
        var numbered = SlideNumberRegex().Match(text);
        if (numbered.Success && numbered.Groups[1].Value.Trim().Length > 0) text = numbered.Groups[1].Value.Trim();
        return text.Length > 150 ? text[..OfficeDocumentWriter.SafeCut(text, 150)] + "…" : text;
    }

    [GeneratedRegex(@"^(?:слайд|slide)\s*№?\s*\d+\s*[:.\-–—]\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SlideNumberRegex();

    // ------------------------------------------------------------------ tables

    private static int TableFontSize(int columns) => columns switch
    {
        <= 4 => 1600,
        <= 6 => 1400,
        <= 8 => 1200,
        _ => 1000,
    };

    private static long[] TableColumnWidths(MdTable table)
    {
        var columns = table.ColumnCount;
        var weights = new double[columns];
        foreach (var row in table.Rows)
        {
            for (var c = 0; c < columns && c < row.Count; c++)
            {
                weights[c] = Math.Max(weights[c], Math.Clamp(row[c].Length, 4, 40));
            }
        }
        for (var c = 0; c < columns; c++) weights[c] = Math.Max(weights[c], 4);
        var total = weights.Sum();
        var widths = weights.Select(w => (long)(ContentWidth * w / total)).ToArray();
        widths[^1] += ContentWidth - widths.Sum();
        return widths;
    }

    private static long RowHeight(MdTable table, int row, long[] widths, int fontSize)
    {
        var charWidth = fontSize / 100.0 * 0.55 * EmuPerPoint;
        var lines = 1;
        for (var c = 0; c < widths.Length; c++)
        {
            var text = c < table.Rows[row].Count ? table.Rows[row][c] : string.Empty;
            var perLine = Math.Max(1, (int)((widths[c] - 182_880) / charWidth));
            var cellLines = text.Split('\n').Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / (double)perLine)));
            lines = Math.Max(lines, cellLines);
        }
        return (long)(lines * fontSize / 100.0 * 1.2 * EmuPerPoint) + 91_440;
    }

    /// <summary>Row indexes (excluding the header, which repeats) for each slide of a long table.</summary>
    private static IEnumerable<IReadOnlyList<int>> TablePages(MdTable table)
    {
        var widths = TableColumnWidths(table);
        var fontSize = TableFontSize(widths.Length);
        var header = RowHeight(table, 0, widths, fontSize);
        var page = new List<int>();
        var height = header;
        for (var r = 1; r < table.Cells.Count; r++)
        {
            var rowHeight = RowHeight(table, r, widths, fontSize);
            if (page.Count > 0 && height + rowHeight > BodyHeight)
            {
                yield return page;
                page = new List<int>();
                height = header;
            }
            page.Add(r);
            height += rowHeight;
        }
        yield return page;
    }

    // ------------------------------------------------------------------ slides

    private sealed class SlideBuilder
    {
        private readonly SlidePart _part;
        private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
        private uint _nextShapeId = 2;

        public SlideBuilder(SlidePart part)
        {
            _part = part;
        }

        public P.Slide TitleSlide(TitleSlide spec)
        {
            var tree = ShapeTree();
            tree.Append(Placeholder("Title", new P.PlaceholderShape { Type = P.PlaceholderValues.CenteredTitle },
                new[] { TextParagraph(new[] { new MdRun(spec.Title) }) }));
            if (spec.Subtitle.Count > 0)
            {
                tree.Append(Placeholder("Subtitle", new P.PlaceholderShape { Type = P.PlaceholderValues.SubTitle, Index = 1U },
                    spec.Subtitle.Select(runs => TextParagraph(runs))));
            }
            return Slide(tree);
        }

        public P.Slide ContentSlide(ContentSlide spec)
        {
            var tree = ShapeTree();
            tree.Append(TitleShape(spec.Title));
            tree.Append(Placeholder("Content", new P.PlaceholderShape { Index = 1U }, Paragraphs(spec.Lines), autofit: true));
            return Slide(tree);
        }

        public P.Slide TableSlide(TableSlide spec)
        {
            var tree = ShapeTree();
            tree.Append(TitleShape(spec.Title));
            tree.Append(TableFrame(spec.Table, spec.Rows));
            return Slide(tree);
        }

        private static P.Slide Slide(P.ShapeTree tree) =>
            Declare(new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping())));

        private P.Shape TitleShape(string title) =>
            Placeholder("Title", new P.PlaceholderShape { Type = P.PlaceholderValues.Title }, new[] { TextParagraph(new[] { new MdRun(title) }) });

        private P.Shape Placeholder(string name, P.PlaceholderShape placeholder, IEnumerable<A.Paragraph> paragraphs, bool autofit = false)
        {
            var id = _nextShapeId++;
            var body = new P.TextBody(autofit ? new A.BodyProperties(new A.NormalAutoFit()) : new A.BodyProperties(), new A.ListStyle());
            foreach (var paragraph in paragraphs) body.Append(paragraph);
            if (!body.Elements<A.Paragraph>().Any()) body.Append(new A.Paragraph(new A.EndParagraphRunProperties { Language = "ru-RU" }));

            return new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = id, Name = $"{name} {id}" },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties(placeholder)),
                new P.ShapeProperties(),
                body);
        }

        /// <summary>
        /// Numbered items of one sequence share its first number as startAt, which is how PowerPoint
        /// continues a list (Markdown "1. 1. 1." still reads 1, 2, 3; a list continued from the previous
        /// slide starts at its real number).
        /// </summary>
        private IEnumerable<A.Paragraph> Paragraphs(IReadOnlyList<SlideLine> lines)
        {
            var sequenceStart = new int?[9];
            foreach (var line in lines)
            {
                var depth = Math.Clamp(line.Depth, 0, 8);
                for (var deeper = depth + 1; deeper < 9; deeper++) sequenceStart[deeper] = null;
                if (line.Kind == LineKind.Numbered)
                {
                    sequenceStart[depth] ??= Math.Clamp(line.Number, 1, 32767);
                }
                else
                {
                    Array.Clear(sequenceStart, line.Kind == LineKind.Bullet ? depth : 0, line.Kind == LineKind.Bullet ? 9 - depth : 9);
                }
                yield return LineParagraph(line, sequenceStart[depth] ?? 1);
            }
        }

        private A.Paragraph LineParagraph(SlideLine line, int startAt)
        {
            var properties = new A.ParagraphProperties();
            var runs = line.Runs;
            switch (line.Kind)
            {
                case LineKind.Bullet:
                    properties.Level = line.Depth;
                    break;
                case LineKind.Numbered:
                    properties.Level = line.Depth;
                    properties.Append(new A.AutoNumberedBullet { Type = A.TextAutoNumberSchemeValues.ArabicPeriod, StartAt = startAt });
                    break;
                case LineKind.Heading:
                    properties.LeftMargin = 0;
                    properties.Indent = 0;
                    properties.Append(new A.NoBullet());
                    runs = runs.Select(r => r with { Bold = true }).ToList();
                    break;
                case LineKind.Code:
                    properties.LeftMargin = 0;
                    properties.Indent = 0;
                    properties.Append(new A.SpaceBefore(new A.SpacingPoints { Val = 0 }), new A.NoBullet());
                    break;
                case LineKind.Quote:
                    properties.LeftMargin = 228_600;
                    properties.Indent = 0;
                    properties.Append(new A.NoBullet());
                    runs = runs.Select(r => r with { Italic = true }).ToList();
                    break;
                default:
                    properties.LeftMargin = 0;
                    properties.Indent = 0;
                    properties.Append(new A.NoBullet());
                    break;
            }
            return TextParagraph(runs, properties, mono: line.Kind == LineKind.Code);
        }

        private A.Paragraph TextParagraph(IReadOnlyList<MdRun> runs, A.ParagraphProperties? properties = null, bool mono = false, int? fontSize = null, bool bold = false)
        {
            var paragraph = new A.Paragraph();
            if (properties is not null) paragraph.Append(properties);
            var language = OfficeDocumentWriter.LanguageOf(MarkdownBlocks.PlainText(runs));
            foreach (var run in runs)
            {
                var uri = OfficeDocumentWriter.LinkUri(run.Link);
                var text = uri is null ? OfficeDocumentWriter.LinkFallbackText(run) : run.Text;
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) paragraph.Append(new A.Break(RunProperties(run, language, uri, mono, fontSize, bold)));
                    if (lines[i].Length > 0)
                    {
                        paragraph.Append(new A.Run(RunProperties(run, language, uri, mono, fontSize, bold), new A.Text(lines[i])));
                    }
                }
            }
            paragraph.Append(new A.EndParagraphRunProperties { Language = language, FontSize = fontSize, Dirty = false });
            return paragraph;
        }

        private A.RunProperties RunProperties(MdRun run, string language, Uri? link, bool mono, int? fontSize, bool bold)
        {
            var properties = new A.RunProperties { Language = language, Dirty = false };
            if (fontSize is { } size) properties.FontSize = size;
            if (run.Bold || bold) properties.Bold = true;
            if (run.Italic) properties.Italic = true;
            if (run.Strike) properties.Strike = A.TextStrikeValues.SingleStrike;
            if (link is not null) properties.Underline = A.TextUnderlineValues.Single;
            if (run.Code && !mono) properties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = "C7254E" }));
            if (mono || run.Code)
            {
                properties.Append(new A.LatinFont { Typeface = "Consolas" }, new A.ComplexScriptFont { Typeface = "Consolas" });
            }
            if (link is not null)
            {
                if (!_links.TryGetValue(link.AbsoluteUri, out var id))
                {
                    id = _part.AddHyperlinkRelationship(link, true).Id;
                    _links[link.AbsoluteUri] = id;
                }
                properties.Append(new A.HyperlinkOnClick { Id = id });
            }
            return properties;
        }

        private P.GraphicFrame TableFrame(MdTable table, IReadOnlyList<int> rows)
        {
            var widths = TableColumnWidths(table);
            var fontSize = TableFontSize(widths.Length);
            var grid = new A.TableGrid(widths.Select(w => new A.GridColumn { Width = w }));
            var result = new A.Table(new A.TableProperties { FirstRow = true, BandRow = false }, grid);

            long height = 0;
            foreach (var r in rows.Prepend(0))
            {
                var header = r == 0;
                var rowHeight = RowHeight(table, r, widths, fontSize);
                height += rowHeight;
                var row = new A.TableRow { Height = rowHeight };
                for (var c = 0; c < widths.Length; c++)
                {
                    var runs = c < table.Cells[r].Count ? table.Cells[r][c] : Array.Empty<MdRun>();
                    var alignment = table.AlignmentOf(c) switch
                    {
                        MdAlign.Center => A.TextAlignmentTypeValues.Center,
                        MdAlign.Right => A.TextAlignmentTypeValues.Right,
                        _ => A.TextAlignmentTypeValues.Left,
                    };
                    var paragraph = TextParagraph(runs, new A.ParagraphProperties { Alignment = alignment }, fontSize: fontSize, bold: header);
                    row.Append(new A.TableCell(
                        new A.TextBody(new A.BodyProperties(), new A.ListStyle(), paragraph),
                        CellProperties(header)));
                }
                result.Append(row);
            }

            var id = _nextShapeId++;
            return new P.GraphicFrame(
                new P.NonVisualGraphicFrameProperties(
                    new P.NonVisualDrawingProperties { Id = id, Name = $"Table {id}" },
                    new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.Transform(new A.Offset { X = MarginX, Y = BodyTop }, new A.Extents { Cx = ContentWidth, Cy = height }),
                new A.Graphic(new A.GraphicData(result) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
        }

        private static A.TableCellProperties CellProperties(bool header)
        {
            static T Border<T>() where T : A.LinePropertiesType, new()
            {
                var border = new T { Width = 12_700, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center };
                border.Append(new A.SolidFill(new A.RgbColorModelHex { Val = "A6A6A6" }), new A.PresetDash { Val = A.PresetLineDashValues.Solid });
                return border;
            }

            var properties = new A.TableCellProperties(
                Border<A.LeftBorderLineProperties>(),
                Border<A.RightBorderLineProperties>(),
                Border<A.TopBorderLineProperties>(),
                Border<A.BottomBorderLineProperties>(),
                new A.SolidFill(new A.RgbColorModelHex { Val = header ? "D9E2F3" : "FFFFFF" }))
            {
                LeftMargin = 91_440,
                RightMargin = 91_440,
                TopMargin = 45_720,
                BottomMargin = 45_720,
                Anchor = A.TextAnchoringTypeValues.Center,
            };
            return properties;
        }
    }

    /// <summary>Declares the a: and r: prefixes once on the root instead of on every element.</summary>
    private static T Declare<T>(T root) where T : OpenXmlElement
    {
        root.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        if (root is not A.Theme)
        {
            root.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        }
        return root;
    }

    private static P.ShapeTree ShapeTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup(
            new A.Offset { X = 0, Y = 0 },
            new A.Extents { Cx = 0, Cy = 0 },
            new A.ChildOffset { X = 0, Y = 0 },
            new A.ChildExtents { Cx = 0, Cy = 0 })));

    // ------------------------------------------------------------------ master, layouts, theme

    private static SlideLayoutPart AddLayout(SlideMasterPart master, P.SlideLayout layout)
    {
        var part = master.AddNewPart<SlideLayoutPart>();
        part.SlideLayout = Declare(layout);
        part.AddPart(master);
        return part;
    }

    private static P.Shape LayoutPlaceholder(uint id, string name, P.PlaceholderShape placeholder, A.Transform2D? position = null, A.ListStyle? listStyle = null, A.BodyProperties? bodyProperties = null)
    {
        var shapeProperties = new P.ShapeProperties();
        if (position is not null)
        {
            shapeProperties.Append(position, new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
        }
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(placeholder)),
            shapeProperties,
            new P.TextBody(
                bodyProperties ?? new A.BodyProperties(),
                listStyle ?? new A.ListStyle(),
                new A.Paragraph(new A.EndParagraphRunProperties { Language = "ru-RU" })));
    }

    private static A.Transform2D Position(long x, long y, long cx, long cy) =>
        new(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy });

    private static P.SlideLayout TitleSlideLayout()
    {
        var tree = ShapeTree();
        tree.Append(LayoutPlaceholder(2, "Title 1", new P.PlaceholderShape { Type = P.PlaceholderValues.CenteredTitle },
            Position(1_524_000, 1_122_363, 9_144_000, 2_387_600),
            new A.ListStyle(new A.Level1ParagraphProperties(new A.DefaultRunProperties { FontSize = 4400 }) { Alignment = A.TextAlignmentTypeValues.Center }),
            new A.BodyProperties(new A.NormalAutoFit()) { Anchor = A.TextAnchoringTypeValues.Bottom }));
        tree.Append(LayoutPlaceholder(3, "Subtitle 2", new P.PlaceholderShape { Type = P.PlaceholderValues.SubTitle, Index = 1U },
            Position(1_524_000, 3_602_038, 9_144_000, 1_655_762),
            new A.ListStyle(new A.Level1ParagraphProperties(
                new A.NoBullet(),
                new A.DefaultRunProperties(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Text1, })) { FontSize = 2000 })
            {
                LeftMargin = 0,
                Indent = 0,
                Alignment = A.TextAlignmentTypeValues.Center,
            }),
            new A.BodyProperties(new A.NormalAutoFit())));
        return new P.SlideLayout(new P.CommonSlideData(tree) { Name = "Title Slide" }, new P.ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = P.SlideLayoutValues.Title,
            Preserve = true,
        };
    }

    private static P.SlideLayout ContentLayout()
    {
        var tree = ShapeTree();
        tree.Append(LayoutPlaceholder(2, "Title 1", new P.PlaceholderShape { Type = P.PlaceholderValues.Title }));
        tree.Append(LayoutPlaceholder(3, "Content Placeholder 2", new P.PlaceholderShape { Index = 1U }));
        return new P.SlideLayout(new P.CommonSlideData(tree) { Name = "Title and Content" }, new P.ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = P.SlideLayoutValues.Object,
            Preserve = true,
        };
    }

    private static P.SlideLayout TitleOnlyLayout()
    {
        var tree = ShapeTree();
        tree.Append(LayoutPlaceholder(2, "Title 1", new P.PlaceholderShape { Type = P.PlaceholderValues.Title }));
        return new P.SlideLayout(new P.CommonSlideData(tree) { Name = "Title Only" }, new P.ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = P.SlideLayoutValues.TitleOnly,
            Preserve = true,
        };
    }

    private static P.SlideMaster SlideMaster(IReadOnlyList<string> layoutRelationshipIds)
    {
        var tree = ShapeTree();
        tree.Append(LayoutPlaceholder(2, "Title Placeholder 1", new P.PlaceholderShape { Type = P.PlaceholderValues.Title },
            Position(MarginX, TitleTop, ContentWidth, TitleHeight),
            bodyProperties: new A.BodyProperties(new A.NormalAutoFit())
            {
                Vertical = A.TextVerticalValues.Horizontal, LeftInset = 91_440, TopInset = 45_720, RightInset = 91_440, BottomInset = 45_720,
                RightToLeftColumns = false, Anchor = A.TextAnchoringTypeValues.Center,
            }));
        tree.Append(LayoutPlaceholder(3, "Text Placeholder 2", new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U },
            Position(MarginX, BodyTop, ContentWidth, BodyHeight),
            bodyProperties: new A.BodyProperties(new A.NormalAutoFit())
            {
                Vertical = A.TextVerticalValues.Horizontal, LeftInset = 91_440, TopInset = 45_720, RightInset = 91_440, BottomInset = 45_720,
                RightToLeftColumns = false,
            }));

        var layoutIds = new P.SlideLayoutIdList();
        for (var i = 0; i < layoutRelationshipIds.Count; i++)
        {
            layoutIds.Append(new P.SlideLayoutId { Id = 2_147_483_649U + (uint)i, RelationshipId = layoutRelationshipIds[i] });
        }

        var bodyStyle = new P.BodyStyle();
        var sizes = new[] { 2400, 2000, 1800, 1600, 1600, 1600, 1600, 1600, 1600 };
        var bullets = new[] { "•", "–", "•", "–", "•", "–", "•", "–", "•" };
        for (var level = 0; level < 9; level++)
        {
            bodyStyle.Append(LevelProperties(level, 228_600 + 457_200 * level, -228_600, sizes[level], bullets[level], title: false));
        }

        var otherStyle = new P.OtherStyle(LevelProperties(0, 0, 0, 1800, null, title: false));

        return new P.SlideMaster(
            new P.CommonSlideData(
                new P.Background(new P.BackgroundStyleReference(new A.SchemeColor { Val = A.SchemeColorValues.Background1 }) { Index = 1001U }),
                tree),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            layoutIds,
            new P.TextStyles(
                new P.TitleStyle(LevelProperties(0, 0, 0, 3600, null, title: true)),
                bodyStyle,
                otherStyle));
    }

    private static A.TextParagraphPropertiesType LevelProperties(int level, int marginLeft, int indent, int size, string? bullet, bool title)
    {
        A.TextParagraphPropertiesType properties = level switch
        {
            0 => new A.Level1ParagraphProperties(),
            1 => new A.Level2ParagraphProperties(),
            2 => new A.Level3ParagraphProperties(),
            3 => new A.Level4ParagraphProperties(),
            4 => new A.Level5ParagraphProperties(),
            5 => new A.Level6ParagraphProperties(),
            6 => new A.Level7ParagraphProperties(),
            7 => new A.Level8ParagraphProperties(),
            _ => new A.Level9ParagraphProperties(),
        };
        properties.LeftMargin = marginLeft;
        properties.Indent = indent;
        properties.Alignment = A.TextAlignmentTypeValues.Left;
        properties.DefaultTabSize = 914_400;
        properties.RightToLeft = false;
        properties.EastAsianLineBreak = true;
        properties.LatinLineBreak = false;
        properties.Height = true;

        if (title)
        {
            properties.Append(new A.LineSpacing(new A.SpacingPercent { Val = 90_000 }));
            properties.Append(new A.SpaceBefore(new A.SpacingPercent { Val = 0 }));
        }
        else
        {
            properties.Append(new A.SpaceBefore(new A.SpacingPoints { Val = level == 0 ? 1000 : 500 }));
        }

        if (bullet is null)
        {
            properties.Append(new A.NoBullet());
        }
        else
        {
            properties.Append(new A.BulletFont { Typeface = "Arial", PitchFamily = 34, CharacterSet = 0 }, new A.CharacterBullet { Char = bullet });
        }

        var font = title ? "+mj" : "+mn";
        properties.Append(new A.DefaultRunProperties(
            new A.SolidFill(new A.SchemeColor { Val = title ? A.SchemeColorValues.Text2 : A.SchemeColorValues.Text1 }),
            new A.LatinFont { Typeface = font + "-lt" },
            new A.EastAsianFont { Typeface = font + "-ea" },
            new A.ComplexScriptFont { Typeface = font + "-cs" })
        {
            FontSize = size,
            Kerning = 1200,
            Bold = title ? true : null,
        });
        return properties;
    }

    private static P.DefaultTextStyle DefaultTextStyle()
    {
        var style = new P.DefaultTextStyle();
        for (var level = 0; level < 9; level++)
        {
            var properties = LevelProperties(level, 457_200 * level, 0, 1800, null, title: false);
            properties.RemoveAllChildren<A.SpaceBefore>();
            style.Append(properties);
        }
        return style;
    }

    private static A.Theme Theme()
    {
        static A.SolidFill Placeholder() => new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });

        static A.Outline Line(int width) => new(Placeholder(), new A.PresetDash { Val = A.PresetLineDashValues.Solid }, new A.Miter { Limit = 800_000 })
        {
            Width = width,
            CapType = A.LineCapValues.Flat,
            CompoundLineType = A.CompoundLineValues.Single,
            Alignment = A.PenAlignmentValues.Center,
        };

        var colors = new A.ColorScheme(
            new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
            new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = "1F3864" }),
            new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "4472C4" }),
            new A.Accent2Color(new A.RgbColorModelHex { Val = "ED7D31" }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
            new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
            new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
            new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
        {
            Name = "Conexy",
        };

        var fonts = new A.FontScheme(
            new A.MajorFont(new A.LatinFont { Typeface = "Calibri Light" }, new A.EastAsianFont { Typeface = string.Empty }, new A.ComplexScriptFont { Typeface = string.Empty }),
            new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = string.Empty }, new A.ComplexScriptFont { Typeface = string.Empty }))
        {
            Name = "Conexy",
        };

        var format = new A.FormatScheme(
            new A.FillStyleList(Placeholder(), Placeholder(), Placeholder()),
            new A.LineStyleList(Line(6_350), Line(12_700), Line(19_050)),
            new A.EffectStyleList(
                new A.EffectStyle(new A.EffectList()),
                new A.EffectStyle(new A.EffectList()),
                new A.EffectStyle(new A.EffectList())),
            new A.BackgroundFillStyleList(Placeholder(), Placeholder(), Placeholder()))
        {
            Name = "Conexy",
        };

        return new A.Theme(new A.ThemeElements(colors, fonts, format), new A.ObjectDefaults(), new A.ExtraColorSchemeList())
        {
            Name = "Conexy",
        };
    }
}

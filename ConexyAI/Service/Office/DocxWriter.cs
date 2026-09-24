using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Ap = DocumentFormat.OpenXml.ExtendedProperties;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ConexyAI.Service.Office;

// OFFICE_OPENXML: добавлено 2026-09-24 — .docx через DocumentFormat.OpenXml: настоящие стили Word
// (Title, Heading1–6, List Paragraph, Quote, Code Block, Hyperlink), определения нумерации для
// маркированных и нумерованных списков с вложенностью, таблицы с рамками и заливкой шапки, номера
// страниц в колонтитуле и свойства документа (название, автор ConexyAI).
internal static class DocxWriter
{
    // A4 with 3 cm / 1.5 cm margins, in twips.
    private const int PageWidth = 11906;
    private const int PageHeight = 16838;
    private const int MarginLeft = 1701;
    private const int MarginRight = 850;
    private const int MarginVertical = 1134;
    private const int TextWidth = PageWidth - MarginLeft - MarginRight;

    private const string BodyFont = "Calibri";
    private const string HeadingFont = "Calibri Light";
    private const string MonoFont = "Consolas";
    private const string BorderColor = "A6A6A6";
    private const string HeaderFill = "D9E2F3";
    private const int BulletNumberingId = 1;
    private const int MaxListLevels = 9;

    public static byte[] Write(IReadOnlyList<MdBlock> blocks, string fallbackTitle)
    {
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            var builder = new BodyBuilder(main, blocks);
            var body = builder.Build();

            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = PageNumberFooter();
            body.Append(SectionProperties(main.GetIdOfPart(footer)));
            var document = new W.Document(body);
            // Hyperlinks and the footer reference use r:id; declare the prefix once on the root.
            document.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            main.Document = document;

            main.AddNewPart<StyleDefinitionsPart>().Styles = Styles();
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = Numbering(builder.OrderedLists);
            main.AddNewPart<DocumentSettingsPart>().Settings = Settings();

            var title = builder.DocumentTitle ?? fallbackTitle;
            package.PackageProperties.Title = title;
            package.PackageProperties.Creator = OfficeDocumentWriter.Creator;
            package.PackageProperties.LastModifiedBy = OfficeDocumentWriter.Creator;
            package.PackageProperties.Created = DateTime.UtcNow;
            package.PackageProperties.Modified = DateTime.UtcNow;
            package.PackageProperties.Language = OfficeDocumentWriter.LanguageOf(title);
            package.AddExtendedFilePropertiesPart().Properties = new Ap.Properties(new Ap.Application(OfficeDocumentWriter.Creator));
        }
        return stream.ToArray();
    }

    // ------------------------------------------------------------------ body

    private sealed class BodyBuilder
    {
        private readonly MainDocumentPart _main;
        private readonly IReadOnlyList<MdBlock> _blocks;
        private readonly W.Body _body = new();
        private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

        // numId of the ordered sequence open at each list level (null = none).
        private readonly int?[] _orderedAt = new int?[MaxListLevels];

        /// <summary>(level, start) of every ordered list sequence; numIds start at 2.</summary>
        public List<(int Level, int Start)> OrderedLists { get; } = new();

        public string? DocumentTitle { get; private set; }

        public BodyBuilder(MainDocumentPart main, IReadOnlyList<MdBlock> blocks)
        {
            _main = main;
            _blocks = blocks;
        }

        public W.Body Build()
        {
            var firstH1 = _blocks.OfType<MdHeading>().FirstOrDefault(h => h.Level == 1);
            if (firstH1 is not null) DocumentTitle = MarkdownBlocks.PlainText(firstH1.Runs).Trim();

            // A single leading "# …" is the document's title, not its first section.
            var titleBlock = _blocks.Count > 0 && _blocks[0] is MdHeading { Level: 1 } lead
                             && _blocks.OfType<MdHeading>().Count(h => h.Level == 1) == 1
                ? lead
                : null;

            foreach (var block in _blocks)
            {
                if (block is not MdListItem) Array.Clear(_orderedAt);
                switch (block)
                {
                    case MdHeading h when ReferenceEquals(h, titleBlock):
                        _body.Append(Paragraph("Title", h.Runs));
                        break;
                    default:
                        Render(block, _body, inQuote: false);
                        break;
                }
            }

            if (!_body.HasChildren) _body.Append(new W.Paragraph());
            return _body;
        }

        private void Render(MdBlock block, OpenXmlCompositeElement target, bool inQuote)
        {
            switch (block)
            {
                case MdHeading h when inQuote:
                    target.Append(Paragraph("Quote", h.Runs, bold: true));
                    break;
                case MdHeading h:
                    target.Append(Paragraph("Heading" + Math.Clamp(h.Level, 1, 6), h.Runs));
                    break;
                case MdParagraph p:
                    target.Append(Paragraph(inQuote ? "Quote" : null, p.Runs));
                    break;
                case MdListItem li when inQuote:
                    var marker = li.Ordered ? li.Number + ". " : "• ";
                    target.Append(Paragraph("Quote", Prepend(new string(' ', li.Depth * 4) + marker, li.Runs)));
                    break;
                case MdListItem li:
                    target.Append(ListParagraph(li));
                    break;
                case MdTable t:
                    target.Append(Table(t));
                    // Word needs a paragraph between a table and whatever follows it.
                    target.Append(Paragraph(null, Array.Empty<MdRun>(), spacingAfter: 0));
                    break;
                case MdCode c:
                    target.Append(CodeBlock(c));
                    break;
                case MdQuote q:
                    foreach (var inner in q.Blocks) Render(inner, target, inQuote: true);
                    break;
                case MdRule:
                    var rule = Paragraph(null, Array.Empty<MdRun>());
                    rule.ParagraphProperties ??= new W.ParagraphProperties();
                    rule.ParagraphProperties.ParagraphBorders = new W.ParagraphBorders(
                        new W.BottomBorder { Val = W.BorderValues.Single, Size = 6U, Space = 1U, Color = "BFBFBF" });
                    target.Append(rule);
                    break;
            }
        }

        private W.Paragraph ListParagraph(MdListItem item)
        {
            var level = Math.Clamp(item.Depth, 0, MaxListLevels - 1);
            for (var deeper = level + 1; deeper < MaxListLevels; deeper++) _orderedAt[deeper] = null;

            int numId;
            if (!item.Ordered)
            {
                _orderedAt[level] = null;
                numId = BulletNumberingId;
            }
            else if (_orderedAt[level] is { } open)
            {
                numId = open;
            }
            else
            {
                // Every ordered sequence gets its own w:num with a start override, so numbering restarts
                // per list and keeps the number the Markdown started from ("3. …" after a paragraph).
                OrderedLists.Add((level, Math.Clamp(item.Number, 0, 999_999)));
                numId = OrderedLists.Count + 1;
                _orderedAt[level] = numId;
            }

            var runs = item.Checked is { } done ? Prepend(done ? "☑ " : "☐ ", item.Runs) : item.Runs;
            var paragraph = Paragraph("ListParagraph", runs);
            paragraph.ParagraphProperties!.NumberingProperties = new W.NumberingProperties(
                new W.NumberingLevelReference { Val = level },
                new W.NumberingId { Val = numId });
            return paragraph;
        }

        private W.Paragraph Paragraph(string? style, IReadOnlyList<MdRun> runs, bool bold = false, int? spacingAfter = null)
        {
            var properties = new W.ParagraphProperties();
            if (style is not null) properties.ParagraphStyleId = new W.ParagraphStyleId { Val = style };
            if (spacingAfter is { } after) properties.SpacingBetweenLines = new W.SpacingBetweenLines { After = after.ToString() };

            var paragraph = new W.Paragraph(properties);
            AppendRuns(paragraph, runs, bold);
            return paragraph;
        }

        private void AppendRuns(OpenXmlCompositeElement paragraph, IReadOnlyList<MdRun> runs, bool bold)
        {
            foreach (var run in runs)
            {
                var uri = OfficeDocumentWriter.LinkUri(run.Link);
                if (uri is null)
                {
                    paragraph.Append(Run(run with { Text = OfficeDocumentWriter.LinkFallbackText(run) }, bold, hyperlink: false));
                    continue;
                }

                if (!_links.TryGetValue(uri.AbsoluteUri, out var relationshipId))
                {
                    relationshipId = _main.AddHyperlinkRelationship(uri, true).Id;
                    _links[uri.AbsoluteUri] = relationshipId;
                }
                paragraph.Append(new W.Hyperlink(Run(run, bold, hyperlink: true)) { Id = relationshipId, History = true });
            }
        }

        private W.Table Table(MdTable table)
        {
            var columns = table.ColumnCount;
            var (widths, fontSize) = FitColumns(table);

            var properties = new W.TableProperties
            {
                TableStyle = new W.TableStyle { Val = "ConexyTable" },
                TableWidth = new W.TableWidth { Width = TextWidth.ToString(), Type = W.TableWidthUnitValues.Dxa },
                TableBorders = TableBorders(),
                TableLayout = new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                // Only w:val: the per-flag attributes are Office 2010+ and fail strict 2007 validation.
                TableLook = new W.TableLook { Val = "04A0" },
            };
            var result = new W.Table(properties, new W.TableGrid(widths.Select(w => new W.GridColumn { Width = w.ToString() })));

            for (var r = 0; r < table.Cells.Count; r++)
            {
                var header = r == 0;
                var row = new W.TableRow();
                if (header)
                {
                    // Repeat the header on every page and keep it in one piece.
                    row.Append(new W.TableRowProperties(new W.CantSplit(), new W.TableHeader()));
                }

                for (var c = 0; c < columns; c++)
                {
                    var runs = c < table.Cells[r].Count ? table.Cells[r][c] : Array.Empty<MdRun>();
                    var cellProperties = new W.TableCellProperties
                    {
                        TableCellWidth = new W.TableCellWidth { Width = widths[c].ToString(), Type = W.TableWidthUnitValues.Dxa },
                        TableCellVerticalAlignment = new W.TableCellVerticalAlignment { Val = W.TableVerticalAlignmentValues.Center },
                    };
                    if (header)
                    {
                        cellProperties.Shading = new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = HeaderFill };
                    }

                    var paragraph = Paragraph(null, runs, bold: header);
                    paragraph.ParagraphProperties!.SpacingBetweenLines = new W.SpacingBetweenLines
                    {
                        Before = "40", After = "40", Line = "240", LineRule = W.LineSpacingRuleValues.Auto,
                    };
                    var justification = table.AlignmentOf(c) switch
                    {
                        MdAlign.Center => W.JustificationValues.Center,
                        MdAlign.Right => W.JustificationValues.Right,
                        _ => (W.JustificationValues?)null,
                    };
                    if (justification is { } jc) paragraph.ParagraphProperties.Justification = new W.Justification { Val = jc };
                    if (fontSize is not null)
                    {
                        foreach (var run in paragraph.Descendants<W.Run>())
                        {
                            run.RunProperties ??= new W.RunProperties();
                            run.RunProperties.FontSize = new W.FontSize { Val = fontSize };
                            run.RunProperties.FontSizeComplexScript = new W.FontSizeComplexScript { Val = fontSize };
                        }
                    }

                    row.Append(new W.TableCell(cellProperties, paragraph));
                }
                result.Append(row);
            }
            return result;
        }

        private static W.Paragraph CodeBlock(MdCode code)
        {
            var run = new W.Run();
            AppendText(run, code.Text.Length == 0 ? " " : code.Text);
            return new W.Paragraph(new W.ParagraphProperties { ParagraphStyleId = new W.ParagraphStyleId { Val = "CodeBlock" } }, run);
        }
    }

    private static W.Run Run(MdRun run, bool bold, bool hyperlink)
    {
        var properties = new W.RunProperties();
        if (hyperlink) properties.RunStyle = new W.RunStyle { Val = "Hyperlink" };
        else if (run.Code) properties.RunStyle = new W.RunStyle { Val = "InlineCode" };
        if (hyperlink && run.Code) properties.RunFonts = new W.RunFonts { Ascii = MonoFont, HighAnsi = MonoFont, ComplexScript = MonoFont };
        if (run.Bold || bold)
        {
            properties.Bold = new W.Bold();
            properties.BoldComplexScript = new W.BoldComplexScript();
        }
        if (run.Italic)
        {
            properties.Italic = new W.Italic();
            properties.ItalicComplexScript = new W.ItalicComplexScript();
        }
        if (run.Strike) properties.Strike = new W.Strike();

        var result = new W.Run();
        if (properties.HasChildren) result.Append(properties);
        AppendText(result, run.Text);
        return result;
    }

    /// <summary>Text with '\n' as line breaks and '\t' as tabs; spaces are preserved.</summary>
    private static void AppendText(W.Run run, string text)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n' && text[i] != '\t') continue;
            if (i > start) run.Append(new W.Text(text[start..i]) { Space = SpaceProcessingModeValues.Preserve });
            if (i < text.Length) run.Append(text[i] == '\n' ? new W.Break() : new W.TabChar());
            start = i + 1;
        }
    }

    private static IReadOnlyList<MdRun> Prepend(string text, IReadOnlyList<MdRun> runs) =>
        runs.Prepend(new MdRun(text)).ToList();

    /// <summary>
    /// Column widths in twips summing to the text width, and the cell font size (half-points; null for
    /// the normal 11 pt): a table too wide for the page steps down to 9 pt, then 8 pt, before its words
    /// have to break inside a column.
    /// </summary>
    private static (int[] Widths, string? FontSize) FitColumns(MdTable table)
    {
        var sizes = new (string? HalfPoints, double CharWidth)[] { (null, 110), ("18", 90), ("16", 80) };
        foreach (var (halfPoints, charWidth) in sizes)
        {
            var exact = OfficeDocumentWriter.ColumnWidths(table, TextWidth, charWidth, padding: 260, out var fits);
            if (!fits && halfPoints != sizes[^1].HalfPoints) continue;

            var widths = exact.Select(w => (int)Math.Floor(w)).ToArray();
            widths[Array.IndexOf(widths, widths.Max())] += TextWidth - widths.Sum();
            return (widths, halfPoints);
        }
        throw new InvalidOperationException("unreachable");
    }

    private static W.TableBorders TableBorders() => new(
        new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Space = 0U, Color = BorderColor },
        new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U, Space = 0U, Color = BorderColor },
        new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Space = 0U, Color = BorderColor },
        new W.RightBorder { Val = W.BorderValues.Single, Size = 4U, Space = 0U, Color = BorderColor },
        new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U, Space = 0U, Color = BorderColor },
        new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4U, Space = 0U, Color = BorderColor });

    // ------------------------------------------------------------------ parts

    private static W.SectionProperties SectionProperties(string footerId) => new(
        new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = footerId },
        new W.PageSize { Width = (UInt32Value)(uint)PageWidth, Height = (UInt32Value)(uint)PageHeight },
        new W.PageMargin
        {
            Top = MarginVertical, Right = (UInt32Value)(uint)MarginRight, Bottom = MarginVertical, Left = (UInt32Value)(uint)MarginLeft,
            Header = 708U, Footer = 708U, Gutter = 0U,
        },
        new W.Columns { Space = "708" },
        new W.DocGrid { LinePitch = 360 });

    private static W.Footer PageNumberFooter()
    {
        var number = new W.Run(
            new W.RunProperties { Color = new W.Color { Val = "808080" }, FontSize = new W.FontSize { Val = "18" } },
            new W.Text("1"));
        return new W.Footer(new W.Paragraph(
            new W.ParagraphProperties { Justification = new W.Justification { Val = W.JustificationValues.Center } },
            new W.SimpleField(number) { Instruction = " PAGE " }));
    }

    private static W.Settings Settings() => new(
        new W.Zoom { Percent = "100" },
        new W.DefaultTabStop { Val = 708 },
        new W.CharacterSpacingControl { Val = W.CharacterSpacingValues.DoNotCompress },
        new W.Compatibility(new W.CompatibilitySetting
        {
            Name = W.CompatSettingNameValues.CompatibilityMode,
            Uri = "http://schemas.microsoft.com/office/word",
            Val = "15",
        }));

    private static W.Numbering Numbering(IReadOnlyList<(int Level, int Start)> orderedLists)
    {
        var numbering = new W.Numbering(AbstractNumbering(0, bullet: true), AbstractNumbering(1, bullet: false));
        numbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = 0 }) { NumberID = BulletNumberingId });
        for (var i = 0; i < orderedLists.Count; i++)
        {
            var (level, start) = orderedLists[i];
            numbering.Append(new W.NumberingInstance(
                new W.AbstractNumId { Val = 1 },
                new W.LevelOverride(new W.StartOverrideNumberingValue { Val = start }) { LevelIndex = level })
            {
                NumberID = i + 2,
            });
        }
        return numbering;
    }

    private static W.AbstractNum AbstractNumbering(int id, bool bullet)
    {
        var bullets = new[] { "•", "–", "▪" };
        var abstractNum = new W.AbstractNum(
            new W.Nsid { Val = bullet ? "4C6F6E31" : "4C6F6E32" },
            new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel })
        {
            AbstractNumberId = id,
        };

        for (var level = 0; level < MaxListLevels; level++)
        {
            var definition = new W.Level(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = bullet ? W.NumberFormatValues.Bullet : W.NumberFormatValues.Decimal },
                new W.LevelText { Val = bullet ? bullets[level % bullets.Length] : $"%{level + 1}." },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(new W.Indentation { Left = (720 + 360 * level).ToString(), Hanging = "360" }))
            {
                LevelIndex = level,
            };
            if (bullet)
            {
                definition.Append(new W.NumberingSymbolRunProperties(new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" }));
            }
            abstractNum.Append(definition);
        }
        return abstractNum;
    }

    private static W.Styles Styles()
    {
        var styles = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle
                {
                    RunFonts = new W.RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, EastAsia = BodyFont, ComplexScript = BodyFont },
                    FontSize = new W.FontSize { Val = "22" },
                    FontSizeComplexScript = new W.FontSizeComplexScript { Val = "22" },
                    Languages = new W.Languages { Val = "ru-RU", EastAsia = "en-US", Bidi = "ar-SA" },
                }),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle
                {
                    SpacingBetweenLines = new W.SpacingBetweenLines { After = "120", Line = "276", LineRule = W.LineSpacingRuleValues.Auto },
                })));

        styles.Append(ParagraphStyle("Normal", "Normal", basedOn: null, isDefault: true));

        styles.Append(ParagraphStyle("Title", "Title", "Normal",
            paragraph: new W.StyleParagraphProperties
            {
                KeepNext = new W.KeepNext(),
                ParagraphBorders = new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = 8U, Space = 4U, Color = "4472C4" }),
                SpacingBetweenLines = new W.SpacingBetweenLines { After = "240", Line = "240", LineRule = W.LineSpacingRuleValues.Auto },
                ContextualSpacing = new W.ContextualSpacing(),
            },
            run: new W.StyleRunProperties
            {
                RunFonts = new W.RunFonts { Ascii = HeadingFont, HighAnsi = HeadingFont, ComplexScript = HeadingFont },
                Color = new W.Color { Val = "1F3864" },
                Kern = new W.Kern { Val = 28U },
                FontSize = new W.FontSize { Val = "52" },
                FontSizeComplexScript = new W.FontSizeComplexScript { Val = "52" },
            },
            uiPriority: 10));

        var headings = new (int Size, string Color, int Before)[]
        {
            (32, "1F3864", 360), (28, "2F5496", 240), (24, "2F5496", 200), (22, "2F5496", 160), (22, "404040", 160), (22, "404040", 160),
        };
        for (var i = 0; i < headings.Length; i++)
        {
            var (size, color, before) = headings[i];
            styles.Append(ParagraphStyle($"Heading{i + 1}", $"heading {i + 1}", "Normal",
                paragraph: new W.StyleParagraphProperties
                {
                    KeepNext = new W.KeepNext(),
                    KeepLines = new W.KeepLines(),
                    SpacingBetweenLines = new W.SpacingBetweenLines { Before = before.ToString(), After = "120" },
                    OutlineLevel = new W.OutlineLevel { Val = i },
                },
                run: new W.StyleRunProperties
                {
                    RunFonts = i == 0 ? new W.RunFonts { Ascii = HeadingFont, HighAnsi = HeadingFont, ComplexScript = HeadingFont } : null,
                    Bold = new W.Bold(),
                    BoldComplexScript = new W.BoldComplexScript(),
                    Italic = i >= 4 ? new W.Italic() : null,
                    Color = new W.Color { Val = color },
                    FontSize = new W.FontSize { Val = size.ToString() },
                    FontSizeComplexScript = new W.FontSizeComplexScript { Val = size.ToString() },
                },
                uiPriority: 9,
                next: "Normal"));
        }

        styles.Append(ParagraphStyle("ListParagraph", "List Paragraph", "Normal",
            paragraph: new W.StyleParagraphProperties
            {
                SpacingBetweenLines = new W.SpacingBetweenLines { After = "60" },
                Indentation = new W.Indentation { Left = "720" },
                ContextualSpacing = new W.ContextualSpacing(),
            },
            uiPriority: 34));

        styles.Append(ParagraphStyle("Quote", "Quote", "Normal",
            paragraph: new W.StyleParagraphProperties
            {
                ParagraphBorders = new W.ParagraphBorders(new W.LeftBorder { Val = W.BorderValues.Single, Size = 18U, Space = 8U, Color = "BFBFBF" }),
                SpacingBetweenLines = new W.SpacingBetweenLines { Before = "60", After = "60" },
                Indentation = new W.Indentation { Left = "567", Right = "567" },
            },
            run: new W.StyleRunProperties { Italic = new W.Italic(), Color = new W.Color { Val = "595959" } },
            uiPriority: 29));

        styles.Append(ParagraphStyle("CodeBlock", "Code Block", "Normal",
            paragraph: new W.StyleParagraphProperties
            {
                KeepLines = new W.KeepLines(),
                ParagraphBorders = new W.ParagraphBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Space = 4U, Color = "D9D9D9" },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U, Space = 4U, Color = "D9D9D9" },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Space = 4U, Color = "D9D9D9" },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4U, Space = 4U, Color = "D9D9D9" }),
                Shading = new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = "F4F5F7" },
                SpacingBetweenLines = new W.SpacingBetweenLines { Before = "120", After = "160", Line = "240", LineRule = W.LineSpacingRuleValues.Auto },
                Indentation = new W.Indentation { Left = "113", Right = "113" },
            },
            run: new W.StyleRunProperties
            {
                RunFonts = new W.RunFonts { Ascii = MonoFont, HighAnsi = MonoFont, EastAsia = MonoFont, ComplexScript = MonoFont },
                NoProof = new W.NoProof(),
                FontSize = new W.FontSize { Val = "19" },
                FontSizeComplexScript = new W.FontSizeComplexScript { Val = "19" },
            },
            uiPriority: 39));

        styles.Append(CharacterStyle("DefaultParagraphFont", "Default Paragraph Font", run: null, isDefault: true));
        styles.Append(CharacterStyle("Hyperlink", "Hyperlink", new W.StyleRunProperties
        {
            Color = new W.Color { Val = "0563C1" },
            Underline = new W.Underline { Val = W.UnderlineValues.Single },
        }));
        styles.Append(CharacterStyle("InlineCode", "Inline Code", new W.StyleRunProperties
        {
            RunFonts = new W.RunFonts { Ascii = MonoFont, HighAnsi = MonoFont, EastAsia = MonoFont, ComplexScript = MonoFont },
            NoProof = new W.NoProof(),
            Color = new W.Color { Val = "C7254E" },
            FontSize = new W.FontSize { Val = "20" },
            Shading = new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = "F2F2F2" },
        }));

        styles.Append(TableNormalStyle());
        styles.Append(ConexyTableStyle());
        return styles;
    }

    private static W.Style ParagraphStyle(
        string id,
        string name,
        string? basedOn,
        W.StyleParagraphProperties? paragraph = null,
        W.StyleRunProperties? run = null,
        int? uiPriority = null,
        string? next = null,
        bool isDefault = false)
    {
        var style = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id };
        if (isDefault) style.Default = true;
        style.Append(new W.StyleName { Val = name });
        if (basedOn is not null) style.Append(new W.BasedOn { Val = basedOn });
        if (next is not null) style.Append(new W.NextParagraphStyle { Val = next });
        if (uiPriority is { } priority) style.Append(new W.UIPriority { Val = priority });
        style.Append(new W.PrimaryStyle());
        if (paragraph is not null) style.Append(paragraph);
        if (run is not null) style.Append(run);
        return style;
    }

    private static W.Style CharacterStyle(string id, string name, W.StyleRunProperties? run, bool isDefault = false)
    {
        var style = new W.Style { Type = W.StyleValues.Character, StyleId = id };
        if (isDefault) style.Default = true;
        style.Append(new W.StyleName { Val = name });
        if (!isDefault) style.Append(new W.BasedOn { Val = "DefaultParagraphFont" });
        style.Append(new W.UIPriority { Val = isDefault ? 1 : 99 });
        if (isDefault) style.Append(new W.SemiHidden());
        style.Append(new W.UnhideWhenUsed());
        if (run is not null) style.Append(run);
        return style;
    }

    private static W.Style TableNormalStyle()
    {
        var style = new W.Style { Type = W.StyleValues.Table, StyleId = "TableNormal", Default = true };
        style.Append(
            new W.StyleName { Val = "Normal Table" },
            new W.UIPriority { Val = 99 },
            new W.SemiHidden(),
            new W.UnhideWhenUsed(),
            new W.StyleTableProperties(
                new W.TableIndentation { Width = 0, Type = W.TableWidthUnitValues.Dxa },
                new W.TableCellMarginDefault(
                    new W.TopMargin { Width = "0", Type = W.TableWidthUnitValues.Dxa },
                    new W.TableCellLeftMargin { Width = 108, Type = W.TableWidthValues.Dxa },
                    new W.BottomMargin { Width = "0", Type = W.TableWidthUnitValues.Dxa },
                    new W.TableCellRightMargin { Width = 108, Type = W.TableWidthValues.Dxa })));
        return style;
    }

    private static W.Style ConexyTableStyle()
    {
        var style = new W.Style { Type = W.StyleValues.Table, StyleId = "ConexyTable" };
        style.Append(
            new W.StyleName { Val = "Conexy Table" },
            new W.BasedOn { Val = "TableNormal" },
            new W.UIPriority { Val = 59 },
            new W.StyleParagraphProperties
            {
                SpacingBetweenLines = new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto },
            },
            new W.StyleTableProperties(TableBorders()),
            new W.TableStyleProperties(
                new W.RunPropertiesBaseStyle { Bold = new W.Bold(), BoldComplexScript = new W.BoldComplexScript() },
                new W.TableStyleConditionalFormattingTableCellProperties(
                    new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = HeaderFill }))
            {
                Type = W.TableStyleOverrideValues.FirstRow,
            });
        return style;
    }
}

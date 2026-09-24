using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Service.Office;

// OFFICE_FORMATS: добавлено 2026-09-23
// OFFICE_OPENXML: переписано 2026-09-24 — полноценный разбор того подмножества Markdown, которое пишут
// модели: вложенные списки (в т.ч. задачи [ ]/[x]), GFM-таблицы с выравниванием и экранированным «|»,
// ограждённый код с языком, цитаты, горизонтальные линии и inline-разметка (жирный, курсив, зачёркнутый,
// `код`, ссылки). Раньше курсив, код и ссылки терялись, а вложенность списков считалась по «2 пробела».
// The one content model behind .docx/.xlsx/.pptx generation. Models already answer in Markdown, so
// the agents' create_document tool and the "download as…" export of any chat answer both hand over
// Markdown and get the same rendering.

internal abstract record MdBlock;

internal sealed record MdHeading(int Level, IReadOnlyList<MdRun> Runs) : MdBlock;

internal sealed record MdParagraph(IReadOnlyList<MdRun> Runs) : MdBlock;

/// <summary>A list item. <see cref="Depth"/> is the nesting level (0 = top); <see cref="Checked"/> is set for task items.</summary>
internal sealed record MdListItem(IReadOnlyList<MdRun> Runs, bool Ordered, int Number, int Depth, bool? Checked = null) : MdBlock;

internal enum MdAlign
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>
/// A GFM table. The first row is the header. <see cref="Raw"/> keeps each cell's source text (only
/// "\|" unescaped), because a spreadsheet formula such as <c>=B2*C2*D2</c> must not be read as emphasis.
/// </summary>
internal sealed record MdTable(
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MdRun>>> Cells,
    IReadOnlyList<MdAlign> Alignments,
    IReadOnlyList<IReadOnlyList<string>> Raw) : MdBlock
{
    /// <summary>Number of columns (the widest row).</summary>
    public int ColumnCount => Cells.Count == 0 ? 0 : Math.Max(1, Cells.Max(r => r.Count));

    /// <summary>Plain text of every cell, row by row.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows =>
        Cells.Select(r => (IReadOnlyList<string>)r.Select(MarkdownBlocks.PlainText).ToList()).ToList();

    public MdAlign AlignmentOf(int column) => column < Alignments.Count ? Alignments[column] : MdAlign.None;
}

internal sealed record MdCode(string Text, string? Language = null) : MdBlock;

internal sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

internal sealed record MdRule : MdBlock;

/// <summary>
/// A piece of inline text. <see cref="Text"/> may contain '\n' (a hard line break) and '\t'.
/// <see cref="Link"/> is the raw link target, when the text came from <c>[text](url)</c> or an autolink.
/// </summary>
internal sealed record MdRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strike = false,
    string? Link = null)
{
    public bool SameStyle(MdRun other) =>
        Bold == other.Bold && Italic == other.Italic && Code == other.Code && Strike == other.Strike && Link == other.Link;
}

internal static partial class MarkdownBlocks
{
    private const int MaxQuoteNesting = 4;
    private const int MaxListDepth = 8;
    private const int MaxInlineNesting = 8;

    public static IReadOnlyList<MdBlock> Parse(string? markdown) => Parse(markdown, 0);

    private static IReadOnlyList<MdBlock> Parse(string? markdown, int nesting)
    {
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return new BlockParser(lines, nesting).Run();
    }

    /// <summary>Plain text of inline runs (formatting dropped, link targets dropped).</summary>
    public static string PlainText(IEnumerable<MdRun> runs) => string.Concat(runs.Select(r => r.Text));

    // ------------------------------------------------------------------ blocks

    private sealed class BlockParser
    {
        private readonly string[] _lines;
        private readonly int _nesting;
        private readonly List<MdBlock> _blocks = new();
        private readonly List<string> _paragraph = new();

        // Open list state: indentation of each open level, and the item still collecting text.
        private readonly List<int> _listIndents = new();
        private PendingItem? _item;
        private bool _blankAfterItem;

        private sealed class PendingItem
        {
            public required bool Ordered { get; init; }
            public required int Number { get; init; }
            public required int Depth { get; init; }
            public required int ContentIndent { get; init; }
            public bool? Checked { get; init; }
            public List<string> Lines { get; } = new();
        }

        public BlockParser(string[] lines, int nesting)
        {
            _lines = lines;
            _nesting = nesting;
        }

        public List<MdBlock> Run()
        {
            for (var i = 0; i < _lines.Length; i++)
            {
                var line = _lines[i];
                var trimmed = line.Trim();
                var indent = IndentOf(line);

                if (trimmed.Length == 0)
                {
                    FlushParagraph();
                    if (_item is not null) _blankAfterItem = true;
                    continue;
                }

                // Fenced code (``` or ~~~), possibly indented inside a list item.
                var fence = FenceRegex().Match(line);
                if (fence.Success)
                {
                    FlushParagraph();
                    FlushItem();
                    if (indent < 2) EndList();
                    i = ReadFence(i, fence);
                    continue;
                }

                var heading = HeadingRegex().Match(line);
                if (heading.Success)
                {
                    FlushAll();
                    var text = heading.Groups[2].Value.Trim();
                    if (text.Length > 0) _blocks.Add(new MdHeading(heading.Groups[1].Length, Inline(text)));
                    continue;
                }

                if (trimmed.StartsWith('>'))
                {
                    FlushAll();
                    var quoted = new List<string>();
                    for (; i < _lines.Length && _lines[i].TrimStart().StartsWith('>'); i++)
                    {
                        var content = _lines[i].TrimStart()[1..];
                        quoted.Add(content.StartsWith(' ') ? content[1..] : content);
                    }
                    i--;
                    var inner = _nesting < MaxQuoteNesting
                        ? Parse(string.Join('\n', quoted), _nesting + 1)
                        : new List<MdBlock> { new MdParagraph(Inline(string.Join(' ', quoted.Select(q => q.Trim())))) };
                    if (inner.Count > 0) _blocks.Add(new MdQuote(inner));
                    continue;
                }

                if (HorizontalRuleRegex().IsMatch(line))
                {
                    FlushAll();
                    _blocks.Add(new MdRule());
                    continue;
                }

                if (IsTableStart(i))
                {
                    FlushAll();
                    i = ReadTable(i);
                    continue;
                }

                var bullet = BulletRegex().Match(line);
                var ordered = bullet.Success ? Match.Empty : OrderedRegex().Match(line);
                if (bullet.Success || ordered.Success)
                {
                    FlushParagraph();
                    FlushItem();
                    StartItem(line, indent, bullet.Success ? bullet : ordered, ordered.Success);
                    continue;
                }

                // Plain text: continues the open list item, or is a paragraph line.
                if (_item is not null && (!_blankAfterItem || indent >= _item.ContentIndent))
                {
                    if (_blankAfterItem) _item.Lines.Add(string.Empty);
                    _item.Lines.Add(line);
                    _blankAfterItem = false;
                    continue;
                }

                if (_item is not null || _listIndents.Count > 0)
                {
                    FlushItem();
                    EndList();
                }
                _paragraph.Add(line);
            }

            FlushAll();
            return _blocks;
        }

        private void StartItem(string line, int indent, Match match, bool ordered)
        {
            // Depth by indentation: deeper than the parent by 2+ columns opens a level, anything
            // shallower closes levels back to the matching parent. Works for 2-, 3- and 4-space styles.
            if (_listIndents.Count == 0)
            {
                _listIndents.Add(indent);
            }
            else if (indent >= _listIndents[^1] + 2)
            {
                if (_listIndents.Count <= MaxListDepth) _listIndents.Add(indent);
            }
            else
            {
                while (_listIndents.Count > 1 && _listIndents[^1] > indent + 1)
                {
                    _listIndents.RemoveAt(_listIndents.Count - 1);
                }
            }

            var marker = match.Groups[1].Value;
            var text = match.Groups[ordered ? 3 : 2].Value;
            bool? isChecked = null;
            var task = TaskRegex().Match(text);
            if (task.Success)
            {
                isChecked = task.Groups[1].Value != " ";
                text = task.Groups[2].Value;
            }

            var number = 0;
            if (ordered && !int.TryParse(marker, out number)) number = 1;

            _item = new PendingItem
            {
                Ordered = ordered,
                Number = number,
                Depth = Math.Min(MaxListDepth, _listIndents.Count - 1),
                ContentIndent = indent + marker.Length + (ordered ? 2 : 1),
                Checked = isChecked,
            };
            _item.Lines.Add(text);
            _blankAfterItem = false;
        }

        private void FlushItem()
        {
            if (_item is null) return;
            var item = _item;
            _item = null;
            _blankAfterItem = false;
            _blocks.Add(new MdListItem(Inline(JoinLines(item.Lines)), item.Ordered, item.Number, item.Depth, item.Checked));
        }

        private void EndList()
        {
            _listIndents.Clear();
        }

        private void FlushParagraph()
        {
            if (_paragraph.Count == 0) return;
            _blocks.Add(new MdParagraph(Inline(JoinLines(_paragraph))));
            _paragraph.Clear();
        }

        private void FlushAll()
        {
            FlushParagraph();
            FlushItem();
            EndList();
        }

        private int ReadFence(int start, Match fence)
        {
            var marker = fence.Groups[2].Value;
            var language = fence.Groups[3].Value.Trim();
            var fenceIndent = fence.Groups[1].Value.Length;
            var code = new List<string>();
            var i = start + 1;
            for (; i < _lines.Length; i++)
            {
                var closing = _lines[i].Trim();
                if (closing.Length >= marker.Length && closing.All(c => c == marker[0]))
                {
                    break;
                }
                code.Add(StripIndent(_lines[i], fenceIndent));
            }
            _blocks.Add(new MdCode(string.Join('\n', code).TrimEnd('\n', ' '), language.Length == 0 ? null : language));
            return i;
        }

        private bool IsTableStart(int i)
        {
            var trimmed = _lines[i].Trim();
            if (trimmed.StartsWith('|')) return true;
            // "a | b" followed by "---|---" is a table even without the outer pipes (a lone "---" is a rule).
            return trimmed.Contains('|') && i + 1 < _lines.Length && _lines[i + 1].Contains('|')
                   && IsSeparatorRow(SplitRow(_lines[i + 1].Trim()));
        }

        private int ReadTable(int start)
        {
            var rows = new List<IReadOnlyList<IReadOnlyList<MdRun>>>();
            var raw = new List<IReadOnlyList<string>>();
            IReadOnlyList<MdAlign> alignments = Array.Empty<MdAlign>();
            var i = start;
            for (; i < _lines.Length; i++)
            {
                var trimmed = _lines[i].Trim();
                if (trimmed.Length == 0 || !trimmed.Contains('|')) break;
                if (i > start && (HeadingRegex().IsMatch(_lines[i]) || trimmed.StartsWith('>'))) break;

                var cells = SplitRow(trimmed);
                if (IsSeparatorRow(cells))
                {
                    if (rows.Count == 1 && alignments.Count == 0) alignments = cells.Select(AlignmentOf).ToList();
                    continue;
                }
                rows.Add(cells.Select(c => (IReadOnlyList<MdRun>)Inline(c)).ToList());
                raw.Add(cells.Select(c => c.Replace("\\|", "|")).ToList());
            }

            if (rows.Count > 0) _blocks.Add(new MdTable(rows, alignments, raw));
            return i - 1;
        }
    }

    private static int IndentOf(string line)
    {
        var width = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') width++;
            else if (ch == '\t') width += 4 - width % 4;
            else break;
        }
        return width;
    }

    private static string StripIndent(string line, int columns)
    {
        var i = 0;
        while (i < line.Length && i < columns && line[i] == ' ') i++;
        return line[i..];
    }

    /// <summary>Joins source lines of one paragraph: a trailing double space or backslash is a hard break.</summary>
    private static string JoinLines(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || (line.EndsWith('\\') && !line.EndsWith("\\\\", StringComparison.Ordinal));
            var content = line.Trim();
            if (hardBreak && content.EndsWith('\\')) content = content[..^1];
            if (line.Length == 0)
            {
                // A blank line inside a list item separates its paragraphs.
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                continue;
            }
            sb.Append(content);
            if (i < lines.Count - 1) sb.Append(hardBreak ? '\n' : ' ');
        }
        return sb.ToString().Trim(' ');
    }

    /// <summary>Splits a table row on unescaped pipes outside code spans.</summary>
    private static List<string> SplitRow(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < row.Length; i++)
        {
            var ch = row[i];
            if (ch == '\\' && i + 1 < row.Length && row[i + 1] == '|')
            {
                cell.Append("\\|");
                i++;
                continue;
            }
            if (ch == '`') inCode = !inCode;
            if (ch == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }
            cell.Append(ch);
        }
        cells.Add(cell.ToString().Trim());

        // The outer pipes produce an empty first/last cell.
        if (row.StartsWith('|') && cells.Count > 0) cells.RemoveAt(0);
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal) && cells.Count > 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> cells) =>
        cells.Count > 0 && cells.All(c => SeparatorCellRegex().IsMatch(c.Replace(" ", string.Empty)));

    private static MdAlign AlignmentOf(string separator)
    {
        var s = separator.Replace(" ", string.Empty);
        var left = s.StartsWith(':');
        var right = s.EndsWith(':');
        return left && right ? MdAlign.Center : right ? MdAlign.Right : left ? MdAlign.Left : MdAlign.None;
    }

    // ------------------------------------------------------------------ inline

    /// <summary>
    /// Inline Markdown → runs: <c>**bold**</c>/<c>__bold__</c>, <c>*italic*</c>/<c>_italic_</c>,
    /// <c>~~strike~~</c>, <c>`code`</c>, <c>[text](url)</c>, <c>&lt;url&gt;</c>, bare http(s) URLs,
    /// images (their alt text), backslash escapes and <c>&lt;br&gt;</c> line breaks.
    /// </summary>
    public static IReadOnlyList<MdRun> Inline(string text)
    {
        var normalized = BreakTagRegex().Replace(text ?? string.Empty, "\n");
        var runs = new List<MdRun>();
        new InlineParser(normalized, runs).Parse(0, normalized.Length, new MdRun(string.Empty), 0);
        return runs;
    }

    private sealed class InlineParser
    {
        private readonly string _s;
        private readonly List<MdRun> _runs;

        public InlineParser(string s, List<MdRun> runs)
        {
            _s = s;
            _runs = runs;
        }

        public void Parse(int start, int end, MdRun style, int nesting)
        {
            var buffer = new StringBuilder();
            var i = start;
            while (i < end)
            {
                var ch = _s[i];

                if (ch == '\\' && i + 1 < end && char.IsAsciiLetterOrDigit(_s[i + 1]) == false && _s[i + 1] < 128 && !char.IsWhiteSpace(_s[i + 1]))
                {
                    buffer.Append(_s[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == '`')
                {
                    var ticks = RunLength(i, end, '`');
                    var close = FindBacktickClose(i + ticks, end, ticks);
                    if (close >= 0)
                    {
                        Emit(buffer, style);
                        var code = _s[(i + ticks)..close];
                        if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                        Add(style with { Text = code.Replace('\n', ' '), Code = true });
                        i = close + ticks;
                        continue;
                    }
                    buffer.Append(_s, i, ticks);
                    i += ticks;
                    continue;
                }

                if ((ch == '[' || (ch == '!' && i + 1 < end && _s[i + 1] == '[')) && TryLink(i, end, out var textStart, out var textEnd, out var url, out var after))
                {
                    Emit(buffer, style);
                    if (ch == '!')
                    {
                        // Images are not embedded; their alt text keeps the sentence readable.
                        Parse(textStart, textEnd, style, nesting + 1);
                    }
                    else if (nesting < MaxInlineNesting)
                    {
                        Parse(textStart, textEnd, style with { Link = url }, nesting + 1);
                    }
                    else
                    {
                        Add(style with { Text = _s[textStart..textEnd], Link = url });
                    }
                    i = after;
                    continue;
                }

                if (ch == '<')
                {
                    var auto = AutolinkRegex().Match(_s, i, end - i);
                    if (auto.Success && auto.Index == i)
                    {
                        Emit(buffer, style);
                        Add(style with { Text = auto.Groups[1].Value, Link = auto.Groups[1].Value });
                        i += auto.Length;
                        continue;
                    }
                }

                if ((ch == 'h' || ch == 'H') && style.Link is null && (i == 0 || !char.IsLetterOrDigit(_s[i - 1])))
                {
                    var bare = BareUrlRegex().Match(_s, i, end - i);
                    if (bare.Success && bare.Index == i)
                    {
                        var urlText = bare.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '»', '"', '\'');
                        Emit(buffer, style);
                        Add(style with { Text = urlText, Link = urlText });
                        i += urlText.Length;
                        continue;
                    }
                }

                if (ch == '*' || ch == '_' || ch == '~')
                {
                    if (nesting >= MaxInlineNesting || !TryEmphasis(i, end, out var length, out var closeAt))
                    {
                        // An unmatched delimiter run is literal text, all of it.
                        var literal = RunLength(i, end, ch);
                        buffer.Append(_s, i, literal);
                        i += literal;
                        continue;
                    }

                    Emit(buffer, style);
                    var inner = ch == '~'
                        ? style with { Strike = true }
                        : length switch
                        {
                            1 => style with { Italic = true },
                            2 => style with { Bold = true },
                            _ => style with { Bold = true, Italic = true },
                        };
                    Parse(i + length, closeAt, inner, nesting + 1);
                    i = closeAt + length;
                    continue;
                }

                buffer.Append(ch);
                i++;
            }
            Emit(buffer, style);
        }

        private void Emit(StringBuilder buffer, MdRun style)
        {
            if (buffer.Length == 0) return;
            Add(style with { Text = buffer.ToString() });
            buffer.Clear();
        }

        private void Add(MdRun run)
        {
            if (run.Text.Length == 0) return;
            if (_runs.Count > 0 && _runs[^1].SameStyle(run))
            {
                _runs[^1] = _runs[^1] with { Text = _runs[^1].Text + run.Text };
                return;
            }
            _runs.Add(run);
        }

        private int RunLength(int i, int end, char c)
        {
            var n = 0;
            while (i + n < end && _s[i + n] == c) n++;
            return n;
        }

        private int FindBacktickClose(int from, int end, int ticks)
        {
            for (var j = from; j < end; j++)
            {
                if (_s[j] != '`') continue;
                var n = RunLength(j, end, '`');
                if (n == ticks) return j;
                j += n - 1;
            }
            return -1;
        }

        private bool TryLink(int i, int end, out int textStart, out int textEnd, out string url, out int after)
        {
            textStart = textEnd = after = 0;
            url = string.Empty;
            var open = _s[i] == '!' ? i + 1 : i;
            var depth = 0;
            var j = open;
            for (; j < end; j++)
            {
                if (_s[j] == '\\') { j++; continue; }
                if (_s[j] == '[') depth++;
                else if (_s[j] == ']' && --depth == 0) break;
            }
            if (j >= end - 1 || _s[j + 1] != '(') return false;

            var k = j + 2;
            var parens = 1;
            for (; k < end; k++)
            {
                if (_s[k] == '(') parens++;
                else if (_s[k] == ')' && --parens == 0) break;
            }
            if (k >= end) return false;

            var target = _s[(j + 2)..k].Trim();
            // [text](url "title") — the title is dropped.
            var space = target.IndexOfAny(new[] { ' ', '\t' });
            if (space > 0) target = target[..space];
            if (target.StartsWith('<') && target.EndsWith('>')) target = target[1..^1];
            if (target.Length == 0 && _s[i] != '!') return false;

            textStart = open + 1;
            textEnd = j;
            url = target;
            after = k + 1;
            return true;
        }

        /// <summary>
        /// A delimiter run opens emphasis when it is followed by a non-space and a matching closing run
        /// (preceded by a non-space) exists. '_' never opens or closes inside a word (snake_case stays).
        /// </summary>
        private bool TryEmphasis(int i, int end, out int length, out int closeAt)
        {
            closeAt = -1;
            var c = _s[i];
            var run = RunLength(i, end, c);
            length = c == '~' ? 2 : Math.Min(run, 3);
            if (c == '~' && run < 2) return false;
            if (i + length >= end || char.IsWhiteSpace(_s[i + length])) return false;
            if (c == '_' && i > 0 && char.IsLetterOrDigit(_s[i - 1])) return false;

            // Longer runs than the opener (e.g. "****") are literal text.
            if (run > length && c != '~') return false;

            for (var j = i + length; j < end; j++)
            {
                var ch = _s[j];
                if (ch == '\\') { j++; continue; }
                if (ch == '`')
                {
                    var ticks = RunLength(j, end, '`');
                    var close = FindBacktickClose(j + ticks, end, ticks);
                    if (close >= 0) { j = close + ticks - 1; continue; }
                    j += ticks - 1;
                    continue;
                }
                if (ch != c) continue;

                var closing = RunLength(j, end, c);
                if (char.IsWhiteSpace(_s[j - 1]) || j == i + length)
                {
                    j += closing - 1;
                    continue;
                }
                if (c == '_' && j + closing < end && char.IsLetterOrDigit(_s[j + closing]))
                {
                    j += closing - 1;
                    continue;
                }

                if (closing == length || (c == '~' && closing >= 2))
                {
                    closeAt = j;
                    return true;
                }
                if (closing > length)
                {
                    // "**bold *it***": the closer is the last `length` characters of the run.
                    closeAt = j + closing - length;
                    return true;
                }
                if (length == 3 && closing < 3)
                {
                    // "***x**" / "***x*": fall back to the shorter emphasis.
                    length = closing;
                    closeAt = j;
                    return true;
                }
                // A shorter run belongs to nested emphasis ("**a *b* c**"): skip it.
                j += closing - 1;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ regexes

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^\s*([-*+•])[ \t]+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*(\d{1,9})([.)])[ \t]+(.*)$")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"^\[([ xX])\][ \t]+(.*)$")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"^(\s*)(`{3,}|~{3,})[ \t]*([^`\s]*)[^`]*$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex SeparatorCellRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTagRegex();

    [GeneratedRegex(@"\G<((?:https?|mailto|ftp):[^\s<>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex AutolinkRegex();

    [GeneratedRegex(@"\Ghttps?://[^\s<>\[\]`]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRegex();
}

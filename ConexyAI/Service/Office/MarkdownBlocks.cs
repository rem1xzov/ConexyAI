using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Service.Office;

// OFFICE_FORMATS: добавлено 2026-09-23
// The one content model behind .docx/.xlsx/.pptx generation. Models already answer in Markdown, so
// the agents' create_document tool and the "download as…" export of any chat answer both hand over
// Markdown and get the same rendering. Only the subset a model actually writes is supported:
// headings, paragraphs, bullet/numbered lists, GFM tables, fenced code and **bold**.

internal abstract record MdBlock;

internal sealed record MdHeading(int Level, IReadOnlyList<MdRun> Runs) : MdBlock;

internal sealed record MdParagraph(IReadOnlyList<MdRun> Runs) : MdBlock;

internal sealed record MdListItem(IReadOnlyList<MdRun> Runs, bool Ordered, int Number, int Depth) : MdBlock;

internal sealed record MdTable(IReadOnlyList<IReadOnlyList<string>> Rows) : MdBlock;

internal sealed record MdCode(string Text) : MdBlock;

/// <summary>A piece of inline text; <see cref="Bold"/> comes from <c>**…**</c> / <c>__…__</c>.</summary>
internal sealed record MdRun(string Text, bool Bold);

internal static partial class MarkdownBlocks
{
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var number = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdParagraph(Inline(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.AppendLine(lines[i]);
                }
                blocks.Add(new MdCode(code.ToString().TrimEnd('\n')));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<string>>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    var cells = SplitRow(lines[i].Trim());
                    if (!IsSeparatorRow(cells))
                    {
                        rows.Add(cells);
                    }
                }
                i--;
                if (rows.Count > 0) blocks.Add(new MdTable(rows));
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MdHeading(heading.Groups[1].Length, Inline(heading.Groups[2].Value)));
                number = 0;
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(trimmed))
            {
                FlushParagraph();
                continue;
            }

            var depth = Math.Min(3, (line.Length - line.TrimStart().Length) / 2);
            var bullet = BulletRegex().Match(trimmed);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new MdListItem(Inline(bullet.Groups[1].Value), false, 0, depth));
                continue;
            }

            var ordered = OrderedRegex().Match(trimmed);
            if (ordered.Success)
            {
                FlushParagraph();
                number = int.TryParse(ordered.Groups[1].Value, out var n) ? n : number + 1;
                blocks.Add(new MdListItem(Inline(ordered.Groups[2].Value), true, number, depth));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Plain text of inline runs (bold markers dropped).</summary>
    public static string PlainText(IEnumerable<MdRun> runs) => string.Concat(runs.Select(r => r.Text));

    /// <summary>
    /// Inline Markdown → runs. Bold is kept; links become "text (url)"; italics and code ticks are
    /// dropped because none of the three formats needs them to stay readable.
    /// </summary>
    public static IReadOnlyList<MdRun> Inline(string text)
    {
        var cleaned = LinkRegex().Replace(text, m => $"{m.Groups[1].Value} ({m.Groups[2].Value})");
        cleaned = cleaned.Replace("`", string.Empty);

        var runs = new List<MdRun>();
        var parts = BoldRegex().Split(cleaned);
        // Split with one capture group: even indexes are plain text, odd ones the bold content.
        for (var i = 0; i < parts.Length; i++)
        {
            var part = i % 2 == 0 ? StripItalics(parts[i]) : parts[i];
            if (part.Length > 0) runs.Add(new MdRun(part, i % 2 == 1));
        }
        return runs;
    }

    private static string StripItalics(string text) => ItalicRegex().Replace(text, "$1");

    private static List<string> SplitRow(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Split('|').Select(c => PlainText(Inline(c.Trim()))).ToList();
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> cells) =>
        cells.Count > 0 && cells.All(c => SeparatorCellRegex().IsMatch(c.Replace(" ", string.Empty)));

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^[-*+•]\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\d{1,4})[.)]\s+(.*)$")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    // Normalizes both bold spellings to a single capture group so Split alternates text/bold.
    [GeneratedRegex(@"(?:\*\*|__)(.+?)(?:\*\*|__)")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<![\w*])[*_](?![\s*_])(.+?)(?<![\s*_])[*_](?![\w*])")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"^:?-{2,}:?$")]
    private static partial Regex SeparatorCellRegex();
}

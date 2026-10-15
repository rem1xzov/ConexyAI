using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Desktop;

/// <summary>
/// Показывает ответ агента в консоли как оформленный текст: заголовки, списки, `код`, **жирный**,
/// блоки кода, цитаты, таблицы и перенос строк по ширине окна. Текст приходит кусками, поэтому
/// оформляется построчно: законченная строка выводится сразу, недописанная ждёт Flush().
/// </summary>
public sealed partial class MarkdownWriter
{
    private const int Plain = 0, Bold = 1, Code = 2, Link = 3;

    private readonly StringBuilder _buf = new();
    private bool _inCode;
    private bool _firstLine; // маркер «●» уже напечатан на этой строке
    private bool _lastBlank = true;

    public void Begin()
    {
        _buf.Clear();
        _inCode = false;
        _firstLine = true;
        _lastBlank = true;
    }

    public void Feed(string delta)
    {
        _buf.Append(delta);
        while (true)
        {
            var text = _buf.ToString();
            var nl = text.IndexOf('\n');
            if (nl < 0)
                return;
            _buf.Remove(0, nl + 1);
            RenderLine(text[..nl].TrimEnd('\r'));
        }
    }

    public void Flush()
    {
        if (_buf.Length == 0)
            return;
        var line = _buf.ToString().TrimEnd('\r');
        _buf.Clear();
        RenderLine(line);
    }

    // ── Строки ──────────────────────────────────────────────────────────────

    private string Lead()
    {
        if (_firstLine)
        {
            _firstLine = false;
            return string.Empty;
        }
        return "  ";
    }

    private void EnsureLineStart()
    {
        if (_firstLine)
        {
            Console.WriteLine();
            _firstLine = false;
        }
    }

    private void RenderLine(string raw)
    {
        var line = Ui.Sanitize(raw);
        var trimmed = line.Trim();

        if (trimmed.StartsWith("```"))
        {
            _inCode = !_inCode;
            if (_inCode)
            {
                EnsureLineStart();
                var lang = trimmed.TrimStart('`').Trim();
                if (lang.Length > 0)
                    Console.WriteLine("  " + Ui.Muted(lang));
            }
            _lastBlank = false;
            return;
        }

        if (_inCode)
        {
            EnsureLineStart();
            RenderCodeLine(line);
            _lastBlank = false;
            return;
        }

        if (trimmed.Length == 0)
        {
            if (!_firstLine && !_lastBlank)
                Console.WriteLine();
            _lastBlank = !_firstLine;
            return;
        }

        if (HorizontalRule().IsMatch(trimmed))
        {
            EnsureLineStart();
            Console.WriteLine("  " + Ui.Muted(new string('─', Math.Max(10, Ui.Width() - 4))));
            _lastBlank = false;
            return;
        }

        var heading = Heading().Match(line);
        if (heading.Success)
        {
            if (!_firstLine && !_lastBlank)
                Console.WriteLine();
            var level = heading.Groups[1].Length;
            var style = level <= 2 ? "1;38;5;209" : "1;38;5;255";
            var headLead = Lead();
            WrapRuns(Parse(heading.Groups[2].Value), headLead, headLead.Length, "  ", 2, style);
            _lastBlank = false;
            return;
        }

        if (trimmed.StartsWith('|'))
        {
            EnsureLineStart();
            if (!TableSeparator().IsMatch(trimmed))
            {
                var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim());
                Console.WriteLine("  " + string.Join(Ui.Muted("  │  "), cells.Select(c => Render(Parse(c), null))));
            }
            _lastBlank = false;
            return;
        }

        var quote = Quote().Match(line);
        if (quote.Success)
        {
            var quoteLead = Lead();
            WrapRuns(Parse(quote.Groups[1].Value), quoteLead + Ui.Muted("│ "), quoteLead.Length + 2, "  " + Ui.Muted("│ "), 4, "38;5;250");
            _lastBlank = false;
            return;
        }

        var item = ListItem().Match(line);
        if (item.Success)
        {
            var depth = item.Groups[1].Length;
            var marker = item.Groups[2].Value;
            var isBullet = marker is "-" or "*" or "+";
            var shown = isBullet ? "•" : marker;
            var pad = new string(' ', depth);
            var lead = Lead();
            var firstPrefix = lead + pad + Ui.Accent(shown) + " ";
            var firstLen = lead.Length + depth + shown.Length + 1;
            var hang = 2 + depth + shown.Length + 1;
            WrapRuns(Parse(item.Groups[3].Value), firstPrefix, firstLen, new string(' ', hang), hang, null);
            _lastBlank = false;
            return;
        }

        var paraLead = Lead();
        WrapRuns(Parse(line.TrimStart()), paraLead, paraLead.Length, "  ", 2, null);
        _lastBlank = false;
    }

    private static void RenderCodeLine(string line)
    {
        var width = Math.Max(20, Ui.Width() - 4);
        line = line.Replace("\t", "    ");
        if (line.Length == 0)
            line = " ";
        for (var i = 0; i < line.Length; i += width)
        {
            var chunk = line.Substring(i, Math.Min(width, line.Length - i));
            Console.WriteLine("  " + (Ui.Ansi ? $"\x1b[38;5;252;48;5;236m {chunk.PadRight(Math.Min(width, Math.Max(chunk.Length, 1)))} \x1b[0m" : "  " + chunk));
        }
    }

    // ── Инлайн-разметка ─────────────────────────────────────────────────────

    private static List<(string Text, int Kind)> Parse(string text)
    {
        var runs = new List<(string, int)>();
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length > 0)
                runs.Add((plain.ToString(), Plain));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    runs.Add((text.Substring(i + 1, end - i - 1), Code));
                    i = end;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    runs.Add((text.Substring(i + 2, end - i - 2), Bold));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var m = LinkRegex().Match(text, i);
                if (m.Success && m.Index == i)
                {
                    FlushPlain();
                    runs.Add((m.Groups[1].Value, Link));
                    runs.Add((" (" + m.Groups[2].Value + ")", Plain));
                    i += m.Length - 1;
                    continue;
                }
            }

            plain.Append(c);
        }

        FlushPlain();
        return runs;
    }

    private static string Style(string text, int kind, string? baseStyle)
    {
        if (!Ui.Ansi)
            return text;
        return kind switch
        {
            Bold => $"\x1b[1m{text}\x1b[0m",
            Code => $"\x1b[38;5;215;48;5;238m {text} \x1b[0m",
            Link => $"\x1b[4;38;5;80m{text}\x1b[0m",
            _ => baseStyle is null ? text : $"\x1b[{baseStyle}m{text}\x1b[0m",
        };
    }

    private static string Render(List<(string Text, int Kind)> runs, string? baseStyle) =>
        string.Concat(runs.Select(r => Style(r.Text, r.Kind, baseStyle)));

    /// <summary>Печатает размеченный текст с переносом по словам и «висячим» отступом.</summary>
    private static void WrapRuns(
        List<(string Text, int Kind)> runs, string firstPrefix, int firstLen, string nextPrefix, int nextLen, string? baseStyle)
    {
        var tokens = new List<(string Text, int Kind, bool Space)>();
        var space = false;
        foreach (var (text, kind) in runs)
        {
            var parts = text.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    space = true;
                if (parts[i].Length == 0)
                    continue;
                tokens.Add((parts[i], kind, space));
                space = false;
            }
        }

        var width = Ui.Width();
        var sb = new StringBuilder(firstPrefix);
        var col = firstLen;
        var lineStart = firstLen;
        foreach (var (text, kind, sp) in tokens)
        {
            // Код оформлен пробелами по краям — учитываем их в ширине.
            var visible = text.Length + (kind == Code && Ui.Ansi ? 2 : 0);
            var gap = sp && col > lineStart ? 1 : 0;
            if (col > lineStart && col + gap + visible > width)
            {
                Console.WriteLine(sb.ToString());
                sb.Clear().Append(nextPrefix);
                col = lineStart = nextLen;
                gap = 0;
            }
            if (gap == 1)
            {
                sb.Append(' ');
                col++;
            }
            sb.Append(Style(text, kind, baseStyle));
            col += visible;
        }
        Console.WriteLine(sb.ToString());
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\|?[\s:\-]+(\|[\s:\-]+)+\|?$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s*>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex LinkRegex();
}

using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ConexyAI.Service.Web;

/// <summary>Readable text of an HTML page: its title and a Markdown-like body.</summary>
public sealed record ExtractedPage(string? Title, string Text);

// AGENT_WEB_TOOLS: добавлено 2026-09-24
/// <summary>
/// Turns an HTML page into compact readable text for the model: scripts, styles, navigation, page
/// chrome, forms and embedded frames are dropped; headings become Markdown <c>#</c> lines, list items
/// become <c>- </c> lines, table rows become <c>a | b</c> lines, link text is kept and whitespace is
/// collapsed. A real HTML5 parser (AngleSharp) is used because regex stripping breaks on the markup
/// real sites serve (unclosed tags, scripts containing "&lt;/div&gt;", comments, CDATA).
/// </summary>
public static class HtmlTextExtractor
{
    // Всё, что не является содержимым страницы: код, стили, меню, шапка/подвал, формы, встраивания.
    private const string RemovedSelectors =
        "script, style, noscript, template, nav, footer, header, aside, form, svg, iframe, frame, frameset, " +
        "object, embed, canvas, button, select, dialog, [hidden], [aria-hidden='true']";

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "blockquote", "figure", "figcaption", "table", "thead",
        "tbody", "tfoot", "ul", "ol", "dl", "dt", "dd", "address", "details", "summary", "hr", "center",
        "caption", "fieldset", "body",
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HiddenStyle = new(@"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static ExtractedPage Extract(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html ?? string.Empty);

        var title = Collapse(document.Title ?? string.Empty);
        if (title.Length == 0)
        {
            title = Collapse(document.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? string.Empty);
        }
        if (title.Length == 0)
        {
            title = Collapse(document.QuerySelector("h1")?.TextContent ?? string.Empty);
        }

        foreach (var element in document.QuerySelectorAll(RemovedSelectors).ToList())
        {
            element.Remove();
        }
        foreach (var element in document.QuerySelectorAll("[style]").ToList())
        {
            if (HiddenStyle.IsMatch(element.GetAttribute("style") ?? string.Empty))
                element.Remove();
        }

        var root = PickContentRoot(document);
        var writer = new Writer();
        if (root is not null)
        {
            writer.Visit(root, listDepth: 0);
        }

        return new ExtractedPage(title.Length == 0 ? null : title, writer.Finish());
    }

    /// <summary>
    /// Prefers the page's own content container (<c>main</c>, or a single <c>article</c>) over the
    /// whole body when it carries real text, so sidebars and "related" blocks that survived the
    /// removal pass do not dilute the result.
    /// </summary>
    private static IElement? PickContentRoot(IDocument document)
    {
        const int minimumChars = 200;

        var main = document.QuerySelector("main, [role='main']");
        if (main is not null && Collapse(main.TextContent).Length >= minimumChars)
            return main;

        var articles = document.QuerySelectorAll("article");
        if (articles.Length == 1 && Collapse(articles[0].TextContent).Length >= minimumChars)
            return articles[0];

        return document.Body ?? document.DocumentElement;
    }

    private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();

        public void Visit(INode node, int listDepth)
        {
            switch (node)
            {
                case IText text:
                    AppendInline(text.Data);
                    return;
                case IElement element:
                    VisitElement(element, listDepth);
                    return;
            }
        }

        private void VisitElement(IElement element, int listDepth)
        {
            var tag = element.LocalName;
            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                {
                    var heading = Collapse(element.TextContent);
                    if (heading.Length == 0) return;
                    BlockBreak();
                    _sb.Append('#', tag[1] - '0').Append(' ').Append(heading);
                    BlockBreak();
                    return;
                }
                case "br":
                    LineBreak();
                    return;
                case "li":
                {
                    LineBreak();
                    _sb.Append(' ', Math.Max(0, listDepth - 1) * 2).Append("- ");
                    VisitChildren(element, listDepth);
                    LineBreak();
                    return;
                }
                case "ul" or "ol":
                    // Вложенный список продолжает пункт родителя без пустой строки.
                    if (listDepth > 0) LineBreak(); else BlockBreak();
                    VisitChildren(element, listDepth + 1);
                    if (listDepth > 0) LineBreak(); else BlockBreak();
                    return;
                case "tr":
                {
                    var cells = element.Children
                        .Where(c => c.LocalName is "td" or "th")
                        .Select(c => Collapse(c.TextContent))
                        .ToList();
                    if (cells.All(c => c.Length == 0)) return;
                    LineBreak();
                    _sb.Append(string.Join(" | ", cells));
                    LineBreak();
                    return;
                }
                case "pre":
                {
                    var code = element.TextContent.Replace("\r\n", "\n").Trim('\n');
                    if (code.Trim().Length == 0) return;
                    BlockBreak();
                    _sb.Append("```\n").Append(code).Append("\n```");
                    BlockBreak();
                    return;
                }
                case "img":
                {
                    // Картинки модели не видны; подпись alt оставляем, только если она осмысленная.
                    var alt = Collapse(element.GetAttribute("alt") ?? string.Empty);
                    if (alt.Length >= 3) AppendInline($" [изображение: {alt}] ");
                    return;
                }
            }

            if (BlockTags.Contains(tag))
            {
                BlockBreak();
                VisitChildren(element, listDepth);
                BlockBreak();
                return;
            }

            VisitChildren(element, listDepth);
        }

        private void VisitChildren(IElement element, int listDepth)
        {
            foreach (var child in element.ChildNodes)
            {
                Visit(child, listDepth);
            }
        }

        private void AppendInline(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            var text = Whitespace.Replace(raw, " ");
            if (text.Length == 0) return;

            var atLineStart = _sb.Length == 0 || _sb[^1] == '\n';
            var afterSpace = atLineStart || _sb[^1] == ' ';
            if (afterSpace && text[0] == ' ')
            {
                text = text.TrimStart();
                if (text.Length == 0) return;
            }

            _sb.Append(text);
        }

        private void LineBreak()
        {
            TrimTrailingSpaces();
            if (_sb.Length > 0 && _sb[^1] != '\n') _sb.Append('\n');
        }

        private void BlockBreak()
        {
            TrimTrailingSpaces();
            if (_sb.Length == 0) return;
            if (_sb[^1] != '\n') _sb.Append('\n');
            if (_sb.Length < 2 || _sb[^2] != '\n') _sb.Append('\n');
        }

        private void TrimTrailingSpaces()
        {
            var end = _sb.Length;
            while (end > 0 && _sb[end - 1] == ' ') end--;
            _sb.Length = end;
        }

        public string Finish()
        {
            var lines = _sb.ToString()
                .Split('\n')
                .Select(line => line.TrimEnd());
            var joined = string.Join('\n', lines);
            // Пустые пункты списка («- » без текста) — остаток вырезанных иконок/кнопок.
            joined = Regex.Replace(joined, @"^\s*-\s*$\n?", string.Empty, RegexOptions.Multiline);
            return ExtraBlankLines.Replace(joined, "\n\n").Trim();
        }
    }
}

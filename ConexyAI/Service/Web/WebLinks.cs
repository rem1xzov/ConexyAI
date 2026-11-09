using System.Text.RegularExpressions;

namespace ConexyAI.Service.Web;

// USER_LINKS: добавлено 2026-11-09 — «просто скинь ссылку»: пользователь присылает URL, а агент
// открывает страницу и работает с её содержимым, не дожидаясь просьбы «посмотри».
/// <summary>
/// Finds http(s) links the user pasted into a message. Used to offer <c>fetch_web_page</c> (and the
/// browser tools for visual pages such as Figma) even when Smart Search is off, and to tell the model
/// which links the turn is about.
/// </summary>
public static class WebLinks
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""'`]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // URLs in prose often end with punctuation that belongs to the sentence, not to the address.
    private static readonly char[] Trailing = ['.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '»', '"', '\''];

    /// <summary>Distinct absolute http(s) links in order of appearance, capped at <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> Extract(string? text, int max = 5)
    {
        if (string.IsNullOrWhiteSpace(text) || max <= 0)
            return [];

        var found = new List<string>();
        foreach (Match match in UrlRegex.Matches(text))
        {
            var url = match.Value.TrimEnd(Trailing);
            if (url.Length <= "https://".Length)
                continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            if (!found.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
                found.Add(uri.AbsoluteUri);
            if (found.Count >= max)
                break;
        }
        return found;
    }

    /// <summary>
    /// True when the message is mainly a link: once the addresses are removed, little text is left.
    /// Such a turn must open the link first instead of answering from memory.
    /// </summary>
    public static bool IsLinkFocused(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var rest = UrlRegex.Replace(text, " ").Trim();
        return rest.Length <= 120;
    }
}

using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

/// <summary>Arguments for the <c>web_search</c> tool (deserialized from the model's tool call).</summary>
public class WebSearchRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; set; }
}

/// <summary>Outcome of a web search, already formatted as text for the model.</summary>
public class WebSearchResult
{
    public required bool Success { get; set; }
    public required string Output { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Shared OpenAI-style tool schema for <c>web_search</c>, used by both the coder agent
/// (always available) and the flash/pro chat models (only when the Smart Search toggle
/// is enabled for the message).
/// </summary>
public static class WebSearchTool
{
    public const string Name = "web_search";

    public static object Schema() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "Search the internet for up-to-date information — library/API documentation, error solutions, current package versions, anything beyond the model's training data.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query, short and specific" }
                },
                required = new[] { "query" }
            }
        }
    };
}

// AGENT_WEB_TOOLS: добавлено 2026-09-24
/// <summary>
/// Shared OpenAI-style tool schema for <c>fetch_web_page</c>: reads one public page (typically a
/// <c>web_search</c> result) as clean text. The agent modes offer it; the backend is
/// <c>Service.Web.WebPageFetcher</c>.
/// </summary>
public static class FetchWebPageTool
{
    public const string Name = "fetch_web_page";

    public static object Schema() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "Open a public web page (http/https) and read its text: title, final URL after redirects and the readable content " +
                          "(headings as Markdown '#', list items as '- ', link text kept; scripts, navigation, headers/footers and forms removed). " +
                          "Use it to actually read the pages found with web_search — documentation, API references, articles, reports — instead of relying on search snippets. " +
                          "Supports HTML, plain text, Markdown and JSON; internal/private addresses are refused.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", description = "Absolute http(s) URL of the page, e.g. a URL from web_search results" },
                    max_chars = new { type = "integer", description = "Maximum characters of page text to return (default 12000, max 40000)" }
                },
                required = new[] { "url" }
            }
        }
    };
}

namespace ConexyAI.Configuration;

/// <summary>
/// Binds the <c>WebSearch</c> section of appsettings.json. Backs the <c>web_search</c> tool
/// via the JSON SEO API (<c>https://jsonseo.ru/api/yandex</c>). The API key may alternatively
/// be supplied through the <c>JSONSEO_API_KEY</c> environment variable.
/// </summary>
public class WebSearchOptions
{
    public const string SectionName = "WebSearch";

    public string JsonSeoApiKey { get; set; } = string.Empty;
    public string JsonSeoEndpoint { get; set; } = "https://jsonseo.ru/api/yandex";

    /// <summary>Timeout for the HTTP call to the search provider.</summary>
    public int TimeoutSeconds { get; set; } = 12;

    // AGENT_WEB_TOOLS: добавлено 2026-09-24 — границы инструмента fetch_web_page.
    /// <summary>
    /// Whole-fetch budget for <c>fetch_web_page</c>, redirects included. A value &lt;= 0 falls back to
    /// the default (20).
    /// </summary>
    public int FetchTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// How many bytes of a page body <c>fetch_web_page</c> reads (after decompression). The rest of a
    /// larger page is dropped with a note. A value &lt;= 0 falls back to the default (2 MB).
    /// </summary>
    public int FetchMaxBodyBytes { get; set; } = 2 * 1024 * 1024;
}

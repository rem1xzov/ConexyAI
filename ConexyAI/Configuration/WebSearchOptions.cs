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
}

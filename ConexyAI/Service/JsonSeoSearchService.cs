using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

/// <summary>
/// JSON SEO API provider for the <c>web_search</c> tool (Yandex web results over a simple
/// JSON REST contract). Failures are returned as a non-throwing <see cref="WebSearchResult"/>
/// so a search outage never breaks the surrounding agent/chat loop — the model simply
/// continues without fresh results.
/// </summary>
public class JsonSeoSearchService : IWebSearchService
{
    private readonly HttpClient _httpClient;
    private readonly WebSearchOptions _options;
    private readonly ILogger<JsonSeoSearchService> _logger;

    public JsonSeoSearchService(
        HttpClient httpClient,
        IOptions<WebSearchOptions> options,
        ILogger<JsonSeoSearchService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    // H3/INCOGNITO: изменено 2026-09-24 — текст запроса и тело ответа провайдера больше не пишутся в
    // лог: запрос повторяет слова пользователя (в том числе из инкогнито-чата). Только метаданные.
    public async Task<WebSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new WebSearchResult { Success = false, Output = "web_search requires a non-empty 'query'.", Error = "empty_query" };

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return new WebSearchResult
            {
                Success = false,
                Output = "Web search is not configured (missing WebSearch:JsonSeoApiKey or JSONSEO_API_KEY).",
                Error = "not_configured"
            };

        try
        {
            // The query must be URL-encoded: model queries contain spaces, Cyrillic and symbols.
            var url = $"{_options.JsonSeoEndpoint.TrimEnd('/')}?text={Uri.EscapeDataString(query)}&pages=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                _logger.LogError("JSON SEO: insufficient balance (query length {Length})", query.Length);
                return new WebSearchResult { Success = false, Output = "Search provider balance exhausted.", Error = "payment_required" };
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                _logger.LogWarning("JSON SEO: rate limit exceeded (query length {Length})", query.Length);
                return new WebSearchResult { Success = false, Output = "Search rate limit exceeded, try again shortly.", Error = "rate_limited" };
            }

            if (!response.IsSuccessStatusCode)
            {
                // Тело ошибки провайдера помогает понять причину (ключ, лимит), но в логе только его начало.
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                _logger.LogError(
                    "JSON SEO search failed: HTTP {Status} (query length {Length}) {Body}",
                    (int)response.StatusCode, query.Length, body.Length <= 300 ? body : body[..300] + "…");
                return new WebSearchResult { Success = false, Output = $"Search provider returned HTTP {(int)response.StatusCode}.", Error = $"http_{(int)response.StatusCode}" };
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonSeoResponse>(cancellationToken: timeoutCts.Token);
            return FormatResults(query, payload);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("JSON SEO search timed out (query length {Length})", query.Length);
            return new WebSearchResult { Success = false, Output = "Search request timed out.", Error = "timeout" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JSON SEO search failed unexpectedly (query length {Length})", query.Length);
            return new WebSearchResult { Success = false, Output = $"Web search failed: {ex.Message}", Error = "unavailable" };
        }
    }

    private string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.JsonSeoApiKey)) return _options.JsonSeoApiKey;
        return Environment.GetEnvironmentVariable("JSONSEO_API_KEY");
    }

    private static WebSearchResult FormatResults(string query, JsonSeoResponse? payload)
    {
        var items = payload?.Results ?? new List<JsonSeoResultItem>();
        if (items.Count == 0)
            return new WebSearchResult { Success = true, Output = "No search results found." };

        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for \"{query}\":");

        var index = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Url)) continue;

            index++;
            sb.AppendLine($"{index}. {item.Title}");
            sb.AppendLine($"   URL: {item.Url}");
            if (!string.IsNullOrWhiteSpace(item.Passage))
                sb.AppendLine($"   {item.Passage}");
        }

        if (index == 0)
            return new WebSearchResult { Success = true, Output = "No search results found." };

        return new WebSearchResult { Success = true, Output = sb.ToString().Trim() };
    }
}

public class JsonSeoResponse
{
    [JsonPropertyName("results")]
    public List<JsonSeoResultItem>? Results { get; set; }
}

public class JsonSeoResultItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("passage")]
    public string Passage { get; set; } = "";
}

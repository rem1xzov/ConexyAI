using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service.Web;

// DEEPSEEK_SEARCH: добавлено 2026-10-31
/// <summary>
/// Поиск и чтение страниц через встроенный Search/Fetch API DeepSeek
/// (<c>POST /v1/tools/search</c> и <c>/v1/tools/fetch</c>) тем же ключом, что и модели.
/// См. https://api-docs.deepseek.com/guides/search
/// </summary>
internal static class DeepSeekTools
{
    /// <summary>Адрес инструмента на хосте из AiUpstream:DeepSeek:BaseUrl (путь /v1 в базе не важен).</summary>
    public static Uri Endpoint(DeepSeekOptions options, string tool)
    {
        var baseUri = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var parsed)
            ? parsed
            : new Uri("https://api.deepseek.com");
        return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/v1/tools/" + tool);
    }

    public static HttpRequestMessage Request(DeepSeekOptions options, string tool, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(options, tool))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return request;
    }

    /// <summary>Ошибки провайдера (ключ, баланс, лимит, сбой), а не самой страницы.</summary>
    public static bool IsProviderFailure(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.RequestTimeout
            or (HttpStatusCode)429 || (int)status >= 500;
}

/// <summary>Провайдер инструмента <c>web_search</c> на базе DeepSeek Search API.</summary>
public sealed class DeepSeekSearchService : IWebSearchService
{
    private const int ResultCount = 8;
    private const int MaxQueryChars = 128;

    private readonly HttpClient _httpClient;
    private readonly DeepSeekOptions _deepSeek;
    private readonly WebSearchOptions _options;
    private readonly ILogger<DeepSeekSearchService> _logger;

    public DeepSeekSearchService(
        HttpClient httpClient,
        IOptions<AiUpstreamOptions> upstream,
        IOptions<WebSearchOptions> options,
        ILogger<DeepSeekSearchService> logger)
    {
        _httpClient = httpClient;
        _deepSeek = upstream.Value.DeepSeek;
        _options = options.Value;
        _logger = logger;
    }

    // Текст запроса в лог не пишется (он повторяет слова пользователя, в том числе из инкогнито).
    public async Task<WebSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length == 0)
            return new WebSearchResult { Success = false, Output = "web_search requires a non-empty 'query'.", Error = "empty_query" };
        if (string.IsNullOrWhiteSpace(_deepSeek.ApiKey))
            return new WebSearchResult { Success = false, Output = "Web search is not configured (missing DeepSeek API key).", Error = "not_configured" };
        if (query.Length > MaxQueryChars)
            query = query[..MaxQueryChars];

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));
            using var request = DeepSeekTools.Request(_deepSeek, "search", new { query, count = ResultCount });
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _logger.LogError("DeepSeek search failed: HTTP {Status} (query length {Length})", status, query.Length);
                var message = response.StatusCode switch
                {
                    HttpStatusCode.PaymentRequired => "Search provider balance exhausted.",
                    (HttpStatusCode)429 => "Search rate limit exceeded, try again shortly.",
                    _ => $"Search provider returned HTTP {status}.",
                };
                return new WebSearchResult { Success = false, Output = message, Error = $"http_{status}" };
            }

            var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: timeoutCts.Token);
            return Format(query, payload?.Results);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("DeepSeek search timed out (query length {Length})", query.Length);
            return new WebSearchResult { Success = false, Output = "Search request timed out.", Error = "timeout" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "DeepSeek search failed unexpectedly (query length {Length})", query.Length);
            return new WebSearchResult { Success = false, Output = $"Web search failed: {ex.Message}", Error = "unavailable" };
        }
    }

    private static WebSearchResult Format(string query, List<SearchItem>? items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for \"{query}\":");
        var index = 0;
        foreach (var item in items ?? new List<SearchItem>())
        {
            // SEC_SEARCH_HREF: только http(s) и без переводов строк (см. SearchResultText).
            var url = SearchResultText.OneLine(item.Url);
            if (!SearchResultText.IsHttpUrl(url)) continue;
            index++;
            sb.AppendLine($"{index}. {SearchResultText.OneLine(item.Title)}");
            sb.AppendLine($"   URL: {url}");
            if (!string.IsNullOrWhiteSpace(item.Date))
                sb.AppendLine($"   Date: {SearchResultText.OneLine(item.Date)}");
            var text = string.IsNullOrWhiteSpace(item.Summary) ? item.Snippet : item.Summary;
            if (!string.IsNullOrWhiteSpace(text))
                sb.AppendLine($"   {SearchResultText.OneLine(text)}");
        }

        return index == 0
            ? new WebSearchResult { Success = true, Output = "No search results found." }
            : new WebSearchResult { Success = true, Output = sb.ToString().Trim() };
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("results")] public List<SearchItem>? Results { get; set; }
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("snippet")] public string Snippet { get; set; } = "";
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";
        [JsonPropertyName("date")] public string? Date { get; set; }
    }
}

/// <summary>
/// <c>fetch_web_page</c> через DeepSeek Fetch API: страница приходит готовым Markdown, а запрос уходит
/// с серверов DeepSeek (наш сервер к сайту не ходит). Если упал сам провайдер (ключ, баланс, лимит,
/// сбой), страница читается локальным <see cref="WebPageFetcher"/>; «страница недоступна» — ответ окончательный.
/// </summary>
public sealed class DeepSeekPageFetcher : IWebPageFetcher
{
    private readonly HttpClient _httpClient;
    private readonly WebPageFetcher _local;
    private readonly DeepSeekOptions _deepSeek;
    private readonly WebSearchOptions _options;
    private readonly ILogger<DeepSeekPageFetcher> _logger;

    public DeepSeekPageFetcher(
        IHttpClientFactory httpClientFactory,
        WebPageFetcher local,
        IOptions<AiUpstreamOptions> upstream,
        IOptions<WebSearchOptions> options,
        ILogger<DeepSeekPageFetcher> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(DeepSeekPageFetcher));
        _local = local;
        _deepSeek = upstream.Value.DeepSeek;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WebPageFetchResult> FetchAsync(string url, int? maxChars, CancellationToken ct = default)
    {
        var raw = (url ?? string.Empty).Trim();
        if (raw.Length == 0)
            return new(WebPageFetchStatus.PageError, "fetch_web_page requires a 'url'.", "empty_url");
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return new(WebPageFetchStatus.PageError, $"Некорректный URL: '{url}'.", "bad_url");
        if (string.IsNullOrWhiteSpace(_deepSeek.ApiKey))
            return await _local.FetchAsync(raw, maxChars, ct);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.FetchTimeoutSeconds + 10)));
            using var request = DeepSeekTools.Request(_deepSeek, "fetch", new { url = uri.AbsoluteUri });
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var page = await response.Content.ReadFromJsonAsync<FetchResponse>(cancellationToken: timeoutCts.Token);
                var content = page?.Content ?? string.Empty;
                if (content.Trim().Length == 0)
                    return new(WebPageFetchStatus.PageError, "Страница пустая: на ней нет текста.", "empty_page", uri.AbsoluteUri);
                return Success(uri.AbsoluteUri, page?.Title, content, maxChars);
            }

            var status = response.StatusCode;
            if (DeepSeekTools.IsProviderFailure(status))
            {
                _logger.LogWarning("DeepSeek fetch unavailable (HTTP {Status}); reading the page locally.", (int)status);
                return await _local.FetchAsync(raw, maxChars, ct);
            }

            var reason = status == HttpStatusCode.Forbidden
                ? "доступ к странице запрещён политикой безопасности"
                : $"страницу не удалось прочитать (HTTP {(int)status})";
            return new(WebPageFetchStatus.PageError, $"Не удалось открыть {uri.AbsoluteUri}: {reason}.", $"http_{(int)status}", uri.AbsoluteUri);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(WebPageFetchStatus.NetworkError, $"Чтение {uri.AbsoluteUri} превысило время ожидания.", "timeout", uri.AbsoluteUri);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DeepSeek fetch failed; reading the page locally.");
            return await _local.FetchAsync(raw, maxChars, ct);
        }
    }

    private static WebPageFetchResult Success(string url, string? title, string content, int? maxChars)
    {
        var budget = Math.Clamp(maxChars ?? WebPageFetcher.DefaultMaxChars, 500, WebPageFetcher.MaxCharsCap);
        var truncated = content.Length > budget;
        var body = truncated ? content[..budget] : content;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            sb.AppendLine($"Title: {title}");
        sb.AppendLine($"URL: {url}");
        sb.AppendLine();
        sb.Append(body);
        if (truncated)
            sb.AppendLine().Append($"[…обрезано: показано {budget} из {content.Length} символов]");
        return new(WebPageFetchStatus.Success, sb.ToString(), null, url, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private sealed class FetchResponse
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}

/// <summary>
/// SEC_SEARCH_HREF: поля выдачи поиска — чужой текст. Перевод строки в заголовке/выжимке позволял
/// подделать строку «URL: javascript:…» в формате, который разбирает шторка «Результаты поиска»,
/// поэтому каждое поле сводится к одной строке, а результат без http(s)-адреса отбрасывается.
/// </summary>
internal static class SearchResultText
{
    public static string OneLine(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split(new[] { '\r', '\n', '\u2028', '\u2029' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0));

    public static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.Contains(' ')
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

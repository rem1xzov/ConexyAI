using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConexyAI.Desktop;

public sealed class AuthRejectedException(string message) : Exception(message);

public sealed record RunResult(Guid TaskId, string Status, string? Result);

/// <summary>HTTP-часть: обмен ключа на JWT (с кэшем до истечения) и запуск задач агента.</summary>
public sealed class ServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _key;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public string BaseUrl { get; }

    public ServerClient(string baseUrl, string key)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _key = key;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc - TimeSpan.FromMinutes(1))
                return _token;

            using var response = await _http.PostAsJsonAsync("api/device/token", new { key = _key }, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AuthRejectedException("Ключ недействителен или отозван. Выпусти новый в настройках и выполни: conexy login");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AuthRejectedException("Слишком много попыток входа. Подожди минуту.");
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TokenBody>(Json, ct)
                       ?? throw new InvalidOperationException("Пустой ответ сервера.");
            _token = body.Token;
            _tokenExpiresUtc = body.ExpiresAtUtc;
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Ставит задачу в очередь. Сообщение об ошибке сервера (лимиты, занятость) — в исключении.</summary>
    public async Task<Guid> StartRunAsync(string prompt, Guid chatId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/conexy/run")
        {
            Content = JsonContent.Create(new
            {
                model = "conexy-coder",
                prompt = prompt.Replace("\0", string.Empty),
                chatId = chatId.ToString(),
                useDevice = true,
                reasoningEffort = "high",
            }, options: Json),
        };
        request.Headers.Authorization = new("Bearer", await GetTokenAsync(ct));

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests && text.Contains("LIMIT_EXCEEDED"))
            throw new InvalidOperationException("Лимит тарифа исчерпан. Подробности и пополнение — на сайте.");
        if (response.StatusCode == HttpStatusCode.Conflict && text.Contains("TURN_IN_FLIGHT"))
            throw new InvalidOperationException("Предыдущий ход в этом чате ещё идёт.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Сервер отклонил запрос ({(int)response.StatusCode}): {text}");

        var run = JsonSerializer.Deserialize<RunBody>(text, Json) ?? throw new InvalidOperationException("Пустой ответ сервера.");
        return run.Id;
    }

    public async Task<RunResult?> GetRunAsync(Guid taskId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/conexy/{taskId}");
        request.Headers.Authorization = new("Bearer", await GetTokenAsync(ct));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadFromJsonAsync<RunBody>(Json, ct);
        return body is null ? null : new RunResult(body.Id, body.Status ?? string.Empty, body.Result);
    }

    private string? _gitHubToken;
    private bool _gitHubTokenLoaded;

    /// <summary>GitHub-токен, сохранённый на сайте (null — не задан). Кэшируется на время сессии.</summary>
    public async Task<string?> GetGitHubTokenAsync(CancellationToken ct)
    {
        if (_gitHubTokenLoaded)
            return _gitHubToken;

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/device/github-credential");
        request.Headers.Authorization = new("Bearer", await GetTokenAsync(ct));
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            _gitHubTokenLoaded = true;
            return null;
        }
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GitHubBody>(Json, ct);
        _gitHubToken = body?.Token;
        _gitHubTokenLoaded = true;
        return _gitHubToken;
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }

    private sealed record TokenBody(string Token, DateTime ExpiresAtUtc);

    private sealed record GitHubBody(string? Token);

    private sealed record RunBody(Guid Id, string? Status, string? Result);
}

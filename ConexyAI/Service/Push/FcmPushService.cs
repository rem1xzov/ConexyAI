using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Repository;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service.Push;

// PHONE_AGENT: добавлено 2026-10-29 — push на телефон через FCM HTTP v1. Только data-сообщения: уведомление
// рисует нативный код приложения (кнопки «Разрешить»/«Отклонить» работают без открытия приложения).

public interface IPushService
{
    /// <summary>Настроен ли FCM (иначе все отправки — no-op).</summary>
    bool IsConfigured { get; }

    /// <summary>Шлёт data-сообщение на все устройства пользователя. Возвращает число принятых FCM отправок.</summary>
    Task<int> SendAsync(Guid userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);
}

/// <summary>Push не настроен: всё остальное работает, уведомления при закрытом приложении не приходят.</summary>
public sealed class NoopPushService : IPushService
{
    private readonly ILogger<NoopPushService> _logger;
    private int _warned;

    public NoopPushService(ILogger<NoopPushService> logger) => _logger = logger;

    public bool IsConfigured => false;

    public Task<int> SendAsync(Guid userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            _logger.LogWarning("Push is not configured (Push:Fcm:ProjectId / Push:Fcm:ServiceAccountJson): phone notifications are disabled.");
        return Task.FromResult(0);
    }
}

public sealed class FcmPushService : IPushService
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";
    public const string HttpClientName = "fcm";

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FcmPushService> _logger;
    private readonly string _projectId;
    private readonly ServiceAccount? _account;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _accessTokenExpiresAt;

    private sealed record ServiceAccount(string ClientEmail, RSA Key, string TokenUri);

    public FcmPushService(
        IHttpClientFactory http,
        IServiceScopeFactory scopes,
        IOptions<PushOptions> options,
        ILogger<FcmPushService> logger)
    {
        _http = http;
        _scopes = scopes;
        _logger = logger;
        var fcm = options.Value.Fcm;
        _projectId = (fcm.ProjectId ?? string.Empty).Trim();
        _account = TryLoadAccount(fcm.ServiceAccountJson, logger);
    }

    public bool IsConfigured => _account is not null && _projectId.Length > 0;

    /// <summary>JSON ключа или путь к файлу с ним. Ключи и содержимое в лог не попадают.</summary>
    public static ServiceAccountInfo? ParseAccount(string? jsonOrPath)
    {
        if (string.IsNullOrWhiteSpace(jsonOrPath))
            return null;
        var text = jsonOrPath.Trim();
        if (!text.StartsWith('{'))
            text = File.ReadAllText(text);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var email = root.GetProperty("client_email").GetString();
        var pem = root.GetProperty("private_key").GetString();
        var uri = root.TryGetProperty("token_uri", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pem))
            return null;
        return new ServiceAccountInfo(email, pem, string.IsNullOrWhiteSpace(uri) ? DefaultTokenUri : uri);
    }

    public sealed record ServiceAccountInfo(string ClientEmail, string PrivateKeyPem, string TokenUri);

    private static ServiceAccount? TryLoadAccount(string? jsonOrPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(jsonOrPath))
            return null;
        try
        {
            var info = ParseAccount(jsonOrPath);
            if (info is null)
                return null;
            var rsa = RSA.Create();
            rsa.ImportFromPem(info.PrivateKeyPem);
            return new ServiceAccount(info.ClientEmail, rsa, info.TokenUri);
        }
        catch (Exception ex)
        {
            logger.LogError("Push:Fcm:ServiceAccountJson is invalid ({Error}); push is disabled.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Подписанный RS256 JWT-assertion для обмена на access-токен OAuth2.</summary>
    public static string BuildAssertion(string clientEmail, RSA key, string tokenUri, DateTimeOffset now)
    {
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = clientEmail,
            ["scope"] = Scope,
            ["aud"] = tokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(55).ToUnixTimeSeconds(),
        }));
        var signingInput = $"{header}.{claims}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64Url(signature)}";
    }

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (_account is null)
            return null;
        if (!forceRefresh && _accessToken is not null && DateTime.UtcNow < _accessTokenExpiresAt)
            return _accessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _accessToken is not null && DateTime.UtcNow < _accessTokenExpiresAt)
                return _accessToken;

            var assertion = BuildAssertion(_account.ClientEmail, _account.Key, _account.TokenUri, DateTimeOffset.UtcNow);
            using var request = new HttpRequestMessage(HttpMethod.Post, _account.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
            };
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("FCM OAuth token request failed: HTTP {Status}.", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            // Запас в минуту: токен не должен истечь по пути.
            _accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));
            return _accessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("FCM OAuth token request error: {Error}.", ex.GetType().Name);
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<int> SendAsync(Guid userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return 0;

        IReadOnlyList<string> tokens;
        using (var scope = _scopes.CreateScope())
        {
            tokens = await scope.ServiceProvider.GetRequiredService<IMobilePushTokenRepository>().ListTokensAsync(userId, ct);
        }
        if (tokens.Count == 0)
            return 0;

        var sent = 0;
        var dead = new List<string>();
        foreach (var token in tokens)
        {
            var result = await SendOneAsync(token, data, ct);
            if (result == SendResult.Ok)
                sent++;
            else if (result == SendResult.Unregistered)
                dead.Add(token);
        }

        if (dead.Count > 0)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMobilePushTokenRepository>().RemoveTokensAsync(dead, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not remove {Count} dead push token(s): {Error}.", dead.Count, ex.GetType().Name);
            }
        }
        return sent;
    }

    private enum SendResult { Ok, Unregistered, Failed }

    private async Task<SendResult> SendOneAsync(string token, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            message = new
            {
                token,
                data,
                android = new { priority = "HIGH", ttl = "600s" },
            },
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = await GetAccessTokenAsync(forceRefresh: attempt > 0, ct);
            if (access is null)
                return SendResult.Failed;

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(_projectId)}/messages:send")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

            try
            {
                using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                    return SendResult.Ok;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    continue;

                var body = await response.Content.ReadAsStringAsync(ct);
                if (IsUnregistered(response.StatusCode, body))
                    return SendResult.Unregistered;

                _logger.LogWarning("FCM send failed: HTTP {Status}.", (int)response.StatusCode);
                return SendResult.Failed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("FCM send error: {Error}.", ex.GetType().Name);
                return SendResult.Failed;
            }
        }
        return SendResult.Failed;
    }

    /// <summary>404 или UNREGISTERED в деталях ошибки — токен больше не действует.</summary>
    public static bool IsUnregistered(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.NotFound)
            return true;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return false;
            if (error.TryGetProperty("status", out var st) && st.GetString() == "NOT_FOUND")
                return true;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in details.EnumerateArray())
                {
                    if (d.TryGetProperty("errorCode", out var code) && code.GetString() == "UNREGISTERED")
                        return true;
                }
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }
}

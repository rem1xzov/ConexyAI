using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConexyAI.Configuration;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// MCP_OAUTH: добавлено 2026-11-03 — «Авторизовать ConexyAI» для удалённого MCP-сервера.
//
// Раньше единственным способом доступа был статический Bearer-токен из настроек. Теперь, если сервер
// отвечает 401/403, агент показывает пользователю ссылку, тот открывает её в браузере, проходит
// authorization code + PKCE на сервере авторизации и возвращается на наш callback. Выданные токены
// (вместе с refresh) лежат в БД зашифрованными и подставляются в запросы к MCP от имени пользователя.
//
// Порядок обнаружения по спецификации MCP 2025-06-18:
//   1. WWW-Authenticate → resource_metadata (RFC 9728) — приходит из McpHttpClient;
//   2. /.well-known/oauth-protected-resource[<path>] — метаданные ресурса;
//   3. issuer → /.well-known/oauth-authorization-server[<path>] / openid-configuration (RFC 8414);
//   4. registration_endpoint → динамическая регистрация клиента (RFC 7591).
// Каждый обнаруженный адрес проверяется PublicUrlGuard ДО запроса: иначе 401 от чужого сервера
// превращался бы в SSRF во внутреннюю сеть.

/// <summary>Нормализованный адрес MCP-сервера: хост (с нестандартным портом) и путь без «/» на конце.</summary>
public static class McpServerKey
{
    public static string FromUrl(string? url)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
            return string.Empty;
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return host + port + uri.AbsolutePath.TrimEnd('/');
    }
}

/// <summary>PKCE (RFC 7636): verifier и S256-challenge в base64url без выравнивания.</summary>
public static class McpPkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier ?? string.Empty)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Начатая, но ещё не завершённая авторизация. State — одноразовый секрет.</summary>
public sealed record McpPendingAuthorization(
    string State,
    Guid UserId,
    string ServerId,
    string ServerName,
    string ServerUrl,
    string ServerKey,
    string CodeVerifier,
    string ClientId,
    string TokenEndpoint,
    string Resource,
    DateTimeOffset CreatedAtUtc);

// MCP_OAUTH: pending-авторизации живут в памяти с TTL 10 минут и удаляются при первом использовании.
// В БД они не нужны: начатая и брошенная авторизация не должна оставлять следов и не переживает рестарт.
public sealed class McpOAuthStateStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, McpPendingAuthorization> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public McpOAuthStateStore(TimeProvider time) => _time = time;

    public void Create(McpPendingAuthorization pending)
    {
        Sweep();
        _pending[pending.State] = pending;
    }

    /// <summary>Забирает авторизацию по state ровно один раз; просроченная считается отсутствующей.</summary>
    public bool TryTake(string? state, out McpPendingAuthorization pending)
    {
        pending = null!;
        if (string.IsNullOrWhiteSpace(state) || !_pending.TryRemove(state.Trim(), out var stored))
            return false;
        pending = stored;
        return _time.GetUtcNow() - stored.CreatedAtUtc <= Ttl;
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _pending)
        {
            if (now - pair.Value.CreatedAtUtc > Ttl)
                _pending.TryRemove(pair.Key, out _);
        }
    }
}

public sealed record McpOAuthTokens(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
    [property: JsonPropertyName("tokenEndpoint")] string TokenEndpoint,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("scope")] string? Scope);

public sealed record McpOAuthClientRegistration(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("authorizationEndpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("tokenEndpoint")] string TokenEndpoint,
    [property: JsonPropertyName("registrationEndpoint")] string? RegistrationEndpoint,
    [property: JsonPropertyName("redirectUri")] string RedirectUri,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("scope")] string? Scope);

public sealed record McpOAuthDiscoveryResult(
    bool Success,
    string? Error,
    string? Resource,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? RegistrationEndpoint,
    string? Scope)
{
    public static McpOAuthDiscoveryResult Fail(string error) => new(false, error, null, null, null, null, null);
}

public sealed record McpOAuthStartResult(string? AuthorizeUrl, string? Error, string? Message);

public sealed record McpOAuthCallbackResult(bool Success, string ServerName, string? Error);

public sealed record McpOAuthStatusResult(bool Authorized, DateTimeOffset? ExpiresAt);

// MCP_OAUTH: источник Bearer-токена для McpHttpClient. forceRefresh — сервер ответил 401 на текущий токен.
public interface IMcpTokenProvider
{
    Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default);
}

public interface IMcpOAuthDiscovery
{
    Task<McpOAuthDiscoveryResult> DiscoverAsync(string serverUrl, string? resourceMetadataUrl, CancellationToken ct = default);
}

public interface IMcpOAuthService
{
    /// <summary>Готовит ссылку авторизации: discovery + динамическая регистрация + PKCE + state.</summary>
    Task<McpOAuthStartResult> StartAsync(
        Guid userId, string serverId, string serverName, string serverUrl,
        string? resourceMetadataUrl = null, CancellationToken ct = default);

    /// <summary>Обмен кода на токены по state из callback; state одноразовый.</summary>
    Task<McpOAuthCallbackResult> CompleteAsync(string? code, string? state, CancellationToken ct = default);

    /// <summary>Есть ли у пользователя действующая авторизация этого сервера.</summary>
    Task<McpOAuthStatusResult> GetStatusAsync(Guid userId, string serverUrl, CancellationToken ct = default);

    /// <summary>Провайдер токена для одного сервера (refreshing, без обращения к БД на каждый шаг).</summary>
    IMcpTokenProvider CreateTokenProvider(Guid userId, string serverKey);
}

// MCP_OAUTH_DISCOVERY: сетевые шаги flow вынесены отдельно — их проще проверять без БД.
public sealed class McpOAuthDiscovery : IMcpOAuthDiscovery
{
    private const string ResourceMetadataSuffix = "/.well-known/oauth-protected-resource";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly PublicUrlGuard _guard;
    private readonly ILogger<McpOAuthDiscovery> _logger;

    public McpOAuthDiscovery(HttpClient http, PublicUrlGuard guard, ILogger<McpOAuthDiscovery> logger)
    {
        _http = http;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>SEC_MCP_OAUTH: адрес вызывается только если PublicUrlGuard разрешает его (не внутренний).</summary>
    public static async Task<string?> ValidateEndpointAsync(PublicUrlGuard guard, string? url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
            return "Адрес OAuth-эндпоинта должен быть абсолютным (https://…).";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return $"Схема '{uri.Scheme}' у OAuth-эндпоинта запрещена.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Адрес OAuth-эндпоинта с логином/паролем запрещён.";
        var check = await guard.CheckAsync(uri, ct);
        return check.Allowed ? null : (check.Error ?? "OAuth-эндпоинт не публичный.");
    }

    public static IReadOnlyList<string> ResourceMetadataCandidates(Uri resource, string? fromHeader)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(fromHeader) && Uri.TryCreate(fromHeader.Trim(), UriKind.Absolute, out var fromUri))
            list.Add(fromUri.ToString().TrimEnd('/'));

        var origin = resource.GetLeftPart(UriPartial.Authority);
        var path = resource.AbsolutePath.TrimEnd('/');
        // RFC 9728: path-aware вариант идёт первым — метаданные могут быть привязаны к пути сервера.
        if (path.Length > 1) list.Add(origin + ResourceMetadataSuffix + path);
        list.Add(origin + ResourceMetadataSuffix);
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> AuthorizationServerCandidates(Uri issuer)
    {
        var origin = issuer.GetLeftPart(UriPartial.Authority);
        var path = issuer.AbsolutePath.TrimEnd('/');
        var list = new List<string>();
        if (path.Length > 1)
        {
            list.Add(origin + "/.well-known/oauth-authorization-server" + path);
        }
        list.Add(origin + "/.well-known/oauth-authorization-server");
        if (path.Length > 1)
        {
            list.Add(origin + "/.well-known/openid-configuration" + path);
            list.Add(issuer.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/.well-known/openid-configuration");
        }
        list.Add(origin + "/.well-known/openid-configuration");
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<McpOAuthDiscoveryResult> DiscoverAsync(string serverUrl, string? resourceMetadataUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate((serverUrl ?? string.Empty).Trim(), UriKind.Absolute, out var resource))
            return McpOAuthDiscoveryResult.Fail("URL MCP-сервера должен быть абсолютным (https://…).");

        var check = await _guard.CheckAsync(resource, ct);
        if (!check.Allowed)
            return McpOAuthDiscoveryResult.Fail(check.Error ?? "Адрес MCP-сервера не публичный.");

        var resourceMetadata = await FetchFirstJsonAsync(ResourceMetadataCandidates(resource, resourceMetadataUrl), ct);
        var authorizationServer = GetFirstArrayItem(resourceMetadata, "authorization_servers");
        if (authorizationServer is null || !Uri.TryCreate(authorizationServer, UriKind.Absolute, out var issuer))
            issuer = new Uri(resource.GetLeftPart(UriPartial.Authority));

        var serverMetadata = await FetchFirstJsonAsync(AuthorizationServerCandidates(issuer), ct);
        if (serverMetadata is null)
            return McpOAuthDiscoveryResult.Fail(
                "Не удалось найти метаданные сервера авторизации (/.well-known/oauth-authorization-server или openid-configuration).");

        var authorizationEndpoint = GetString(serverMetadata, "authorization_endpoint");
        var tokenEndpoint = GetString(serverMetadata, "token_endpoint");
        if (authorizationEndpoint is null || tokenEndpoint is null)
            return McpOAuthDiscoveryResult.Fail(
                "Сервер авторизации вернул неполные метаданные: нет authorization_endpoint или token_endpoint.");

        var authError = await ValidateEndpointAsync(_guard, authorizationEndpoint, ct);
        if (authError is not null)
            return McpOAuthDiscoveryResult.Fail("authorization_endpoint отклонён: " + authError);
        var tokenError = await ValidateEndpointAsync(_guard, tokenEndpoint, ct);
        if (tokenError is not null)
            return McpOAuthDiscoveryResult.Fail("token_endpoint отклонён: " + tokenError);

        var registrationEndpoint = GetString(serverMetadata, "registration_endpoint");
        if (registrationEndpoint is not null && await ValidateEndpointAsync(_guard, registrationEndpoint, ct) is not null)
        {
            _logger.LogWarning("MCP OAuth: registration_endpoint не прошёл проверку и будет проигнорирован.");
            registrationEndpoint = null;
        }

        // RFC 9728: scopes ресурса приоритетнее общих scopes сервера авторизации.
        var scope = GetSpaceJoined(resourceMetadata, "scopes_supported") ?? GetSpaceJoined(serverMetadata, "scopes_supported");
        var resourceId = GetString(resourceMetadata, "resource") ?? resource.GetLeftPart(UriPartial.Path);

        return new McpOAuthDiscoveryResult(
            true, null, resourceId, authorizationEndpoint, tokenEndpoint, registrationEndpoint, scope);
    }

    private async Task<JsonElement?> FetchFirstJsonAsync(IEnumerable<string> candidates, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            var rejection = await ValidateEndpointAsync(_guard, candidate, ct);
            if (rejection is not null)
            {
                _logger.LogDebug("MCP OAuth: пропускаю эндпоинт {Endpoint}: {Reason}", candidate, rejection);
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) continue;

                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(body)) continue;
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                return document.RootElement.Clone();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "MCP OAuth: эндпоинт {Endpoint} недоступен", candidate);
            }
        }

        return null;
    }

    private static string? GetString(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        return value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;
    }

    private static string? GetFirstArrayItem(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        if (!value.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in found.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                return item.GetString();
        }
        return null;
    }

    private static string? GetSpaceJoined(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        if (!value.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.Array) return null;
        var items = new List<string>();
        foreach (var item in found.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                items.Add(item.GetString()!);
        }
        return items.Count == 0 ? null : string.Join(' ', items);
    }
}

// MCP_OAUTH: основной сервис — регистрация клиента, authorize URL, обмен кода, хранение и refresh токенов.
public sealed class McpOAuthService : IMcpOAuthService
{
    private readonly McpOAuthOptions _options;
    private readonly IMcpOAuthDiscovery _discovery;
    private readonly McpOAuthStateStore _states;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpOAuthService> _logger;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public McpOAuthService(
        IOptions<McpOAuthOptions> options,
        IMcpOAuthDiscovery discovery,
        McpOAuthStateStore states,
        IServiceScopeFactory scopeFactory,
        ISecretProtector protector,
        IHttpClientFactory httpClientFactory,
        ILogger<McpOAuthService> logger)
    {
        _options = options.Value;
        _discovery = discovery;
        _states = states;
        _scopeFactory = scopeFactory;
        _protector = protector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<McpOAuthStartResult> StartAsync(
        Guid userId, string serverId, string serverName, string serverUrl,
        string? resourceMetadataUrl = null, CancellationToken ct = default)
    {
        var url = (serverUrl ?? string.Empty).Trim();
        var serverKey = McpServerKey.FromUrl(url);
        if (serverKey.Length == 0)
            return new McpOAuthStartResult(null, "MCP_URL_REJECTED", "URL MCP-сервера должен быть абсолютным (https://…).");

        var discovery = await _discovery.DiscoverAsync(url, resourceMetadataUrl, ct);
        if (!discovery.Success)
            return new McpOAuthStartResult(null, "MCP_OAUTH_DISCOVERY_FAILED", discovery.Error);

        var registrationResult = await GetOrCreateRegistrationAsync(userId, serverKey, discovery, ct);
        if (registrationResult.Registration is null)
            return new McpOAuthStartResult(null, registrationResult.ErrorCode, registrationResult.Error);
        var registration = registrationResult.Registration;

        var verifier = McpPkce.CreateVerifier();
        var state = McpPkce.CreateState();
        var pending = new McpPendingAuthorization(
            state, userId, serverId, serverName, url, serverKey, verifier,
            registration.ClientId, registration.TokenEndpoint, registration.Resource, DateTimeOffset.UtcNow);
        _states.Create(pending);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = registration.ClientId,
            ["redirect_uri"] = registration.RedirectUri,
            ["code_challenge"] = McpPkce.CreateChallenge(verifier),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            // RFC 8707: ресурс, для которого запрашивается токен.
            ["resource"] = registration.Resource,
        };
        if (!string.IsNullOrWhiteSpace(registration.Scope))
            query["scope"] = registration.Scope;

        _logger.LogInformation(
            "MCP OAuth: authorization started for {Server} (server key {ServerKey}).", serverName, serverKey);
        return new McpOAuthStartResult(
            QueryHelpers.AddQueryString(registration.AuthorizationEndpoint, query), null, null);
    }

    public async Task<McpOAuthCallbackResult> CompleteAsync(string? code, string? state, CancellationToken ct = default)
    {
        // STATE_ONCE: state забирается из хранилища до любых сетевых вызовов — повторный callback
        // с той же ссылкой уже ничего не сделает.
        if (!_states.TryTake(state, out var pending))
            return new McpOAuthCallbackResult(false, string.Empty,
                "Ссылка авторизации устарела или уже использована. Запустите авторизацию из настроек MCP ещё раз.");

        if (string.IsNullOrWhiteSpace(code))
            return new McpOAuthCallbackResult(false, pending.ServerName,
                "Сервер авторизации не вернул код доступа. Попробуйте ещё раз.");

        var (tokens, error) = await ExchangeCodeAsync(pending, code.Trim(), ct);
        if (tokens is null)
            return new McpOAuthCallbackResult(false, pending.ServerName, error);

        await SaveTokensAsync(pending.UserId, pending.ServerKey, tokens, ct);
        _logger.LogInformation("MCP OAuth: server {Server} authorized for user {UserId}.", pending.ServerName, pending.UserId);
        return new McpOAuthCallbackResult(true, pending.ServerName, null);
    }

    public async Task<McpOAuthStatusResult> GetStatusAsync(Guid userId, string serverUrl, CancellationToken ct = default)
    {
        var serverKey = McpServerKey.FromUrl(serverUrl);
        if (serverKey.Length == 0)
            return new McpOAuthStatusResult(false, null);

        var row = await ReadRowAsync(userId, serverKey, ct);
        var tokens = ParseTokens(_protector.Unprotect(row?.Tokens));
        if (tokens is null)
            return new McpOAuthStatusResult(false, null);

        var now = DateTimeOffset.UtcNow;
        var usable = tokens.ExpiresAtUtc is null
            || tokens.ExpiresAtUtc > now
            || !string.IsNullOrWhiteSpace(tokens.RefreshToken);
        return new McpOAuthStatusResult(usable, tokens.ExpiresAtUtc);
    }

    public IMcpTokenProvider CreateTokenProvider(Guid userId, string serverKey) =>
        new McpOAuthTokenProvider(_scopeFactory, _protector, _httpClientFactory, userId, serverKey, _logger);

    // ---------------------------------------------------------------- registration

    private sealed record RegistrationLookup(McpOAuthClientRegistration? Registration, string? ErrorCode, string? Error);

    private async Task<RegistrationLookup> GetOrCreateRegistrationAsync(
        Guid userId, string serverKey, McpOAuthDiscoveryResult discovery, CancellationToken ct)
    {
        var existing = ParseRegistration(_protector.Unprotect((await ReadRowAsync(userId, serverKey, ct))?.ClientRegistration));
        if (existing is not null
            && string.Equals(existing.AuthorizationEndpoint, discovery.AuthorizationEndpoint, StringComparison.Ordinal)
            && string.Equals(existing.TokenEndpoint, discovery.TokenEndpoint, StringComparison.Ordinal)
            && string.Equals(existing.RedirectUri, _options.RedirectUri, StringComparison.Ordinal))
        {
            return new RegistrationLookup(existing, null, null);
        }

        if (string.IsNullOrWhiteSpace(discovery.RegistrationEndpoint))
        {
            return new RegistrationLookup(null, "MCP_OAUTH_NO_REGISTRATION",
                "Сервер авторизации не поддерживает динамическую регистрацию клиента (RFC 7591). " +
                "Нужен заранее зарегистрированный client_id.");
        }

        var registration = await RegisterClientAsync(discovery, ct);
        if (registration is null)
            return new RegistrationLookup(null, "MCP_OAUTH_REGISTRATION_FAILED", _lastRegistrationError);

        await UpsertAsync(userId, serverKey, row => row.ClientRegistration = _protector.Protect(JsonSerializer.Serialize(registration)), ct);
        return new RegistrationLookup(registration, null, null);
    }

    private string? _lastRegistrationError;

    private async Task<McpOAuthClientRegistration?> RegisterClientAsync(McpOAuthDiscoveryResult discovery, CancellationToken ct)
    {
        var payload = new
        {
            client_name = _options.ClientName,
            redirect_uris = new[] { _options.RedirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, discovery.RegistrationEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _httpClientFactory.CreateClient(McpRegistryFactory.UserHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _lastRegistrationError =
                    "Сервер авторизации запретил динамическую регистрацию клиента (HTTP 403). " +
                    "Для этого сервера нужен вручную зарегистрированный клиент.";
                _logger.LogWarning("MCP OAuth: dynamic client registration returned 403 Forbidden.");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _lastRegistrationError = $"Сервер авторизации отклонил регистрацию клиента (HTTP {(int)response.StatusCode}).";
                _logger.LogWarning("MCP OAuth: dynamic client registration failed with HTTP {Status}.", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(body);
            var clientId = document.RootElement.TryGetProperty("client_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                _lastRegistrationError = "Сервер авторизации не вернул client_id при регистрации клиента.";
                return null;
            }

            return new McpOAuthClientRegistration(
                clientId!, discovery.AuthorizationEndpoint!, discovery.TokenEndpoint!,
                discovery.RegistrationEndpoint, _options.RedirectUri,
                discovery.Resource ?? string.Empty, discovery.Scope);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _lastRegistrationError = "Не удалось зарегистрировать клиента у сервера авторизации.";
            _logger.LogWarning(ex, "MCP OAuth: dynamic client registration request failed.");
            return null;
        }
    }

    // ---------------------------------------------------------------- tokens

    private async Task<(McpOAuthTokens? Tokens, string? Error)> ExchangeCodeAsync(
        McpPendingAuthorization pending, string code, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["client_id"] = pending.ClientId,
            ["code_verifier"] = pending.CodeVerifier,
            ["resource"] = pending.Resource,
        };

        var (tokens, error) = await PostTokenRequestAsync(pending.TokenEndpoint, form, ct);
        if (tokens is null)
            return (null, error);

        return (tokens with
        {
            TokenEndpoint = pending.TokenEndpoint,
            ClientId = pending.ClientId,
            Resource = pending.Resource,
        }, null);
    }

    private async Task<(McpOAuthTokens? Tokens, string? Error)> PostTokenRequestAsync(
        string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _httpClientFactory.CreateClient(McpRegistryFactory.UserHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                // PRIVACY_LOGS: ни тело, ни код/токены не пишем — только статус.
                _logger.LogWarning("MCP OAuth: token request failed with HTTP {Status}.", (int)response.StatusCode);
                return (null, $"Сервер авторизации отклонил обмен токена (HTTP {(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var access) && access.ValueKind == JsonValueKind.String
                ? access.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                return (null, "Сервер авторизации вернул ответ без access_token.");

            var refreshToken = root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String
                ? refresh.GetString()
                : null;
            var scope = root.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()
                : null;

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number
                && expires.TryGetDouble(out var seconds) && seconds > 0)
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);

            return (new McpOAuthTokens(accessToken!, refreshToken, expiresAt, tokenEndpoint, string.Empty, string.Empty, scope), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP OAuth: token request failed.");
            return (null, "Не удалось получить токен у сервера авторизации.");
        }
    }

    private async Task SaveTokensAsync(Guid userId, string serverKey, McpOAuthTokens tokens, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(tokens);
        await UpsertAsync(userId, serverKey, row => row.Tokens = _protector.Protect(json), ct);
    }

    // ---------------------------------------------------------------- storage helpers

    private async Task<McpOAuthTokenEntity?> ReadRowAsync(Guid userId, string serverKey, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbConexy>();
        return await db.McpOAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ServerKey == serverKey, ct);
    }

    private async Task UpsertAsync(Guid userId, string serverKey, Action<McpOAuthTokenEntity> apply, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbConexy>();
        var row = await db.McpOAuthTokens
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ServerKey == serverKey, ct);
        if (row is null)
        {
            row = new McpOAuthTokenEntity { UserId = userId, ServerKey = serverKey };
            db.McpOAuthTokens.Add(row);
        }
        apply(row);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    internal static McpOAuthTokens? ParseTokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<McpOAuthTokens>(json); }
        catch (JsonException) { return null; }
    }

    internal static McpOAuthClientRegistration? ParseRegistration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<McpOAuthClientRegistration>(json); }
        catch (JsonException) { return null; }
    }
}

// MCP_OAUTH: держит токены одного сервера в памяти прогона и обновляет их по refresh_token.
// В БД ходит через собственный scope: агентский прогон длинный, общий DbConexy не должен копиться.
internal sealed class McpOAuthTokenProvider : IMcpTokenProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Guid _userId;
    private readonly string _serverKey;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private McpOAuthTokens? _tokens;
    private bool _loaded;

    public McpOAuthTokenProvider(
        IServiceScopeFactory scopeFactory, ISecretProtector protector, IHttpClientFactory httpClientFactory,
        Guid userId, string serverKey, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _httpClientFactory = httpClientFactory;
        _userId = userId;
        _serverKey = serverKey;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                _tokens = McpOAuthService.ParseTokens(_protector.Unprotect((await ReadRowAsync(ct))?.Tokens));
                _loaded = true;
            }

            var tokens = _tokens;
            if (tokens is null) return null;

            var now = DateTimeOffset.UtcNow;
            if (!forceRefresh && (tokens.ExpiresAtUtc is null || tokens.ExpiresAtUtc > now.AddSeconds(60)))
                return tokens.AccessToken;

            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
                return forceRefresh ? null : tokens.AccessToken;

            var refreshed = await RefreshAsync(tokens, ct);
            if (refreshed is not null)
            {
                _tokens = refreshed;
                return refreshed.AccessToken;
            }

            // REFRESH_FAILED: авторизация отозвана или refresh истёк — сервер помечается неавторизованным,
            // чтобы следующий список инструментов снова предложил пользователю войти.
            _tokens = null;
            await ClearTokensAsync(ct);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<McpOAuthTokens?> RefreshAsync(McpOAuthTokens tokens, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken!,
            ["client_id"] = tokens.ClientId,
        };
        if (!string.IsNullOrWhiteSpace(tokens.Resource))
            form["resource"] = tokens.Resource;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokens.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _httpClientFactory.CreateClient(McpRegistryFactory.UserHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MCP OAuth: refresh failed with HTTP {Status} for {ServerKey}.", (int)response.StatusCode, _serverKey);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String)
                return null;

            var refresh = root.TryGetProperty("refresh_token", out var refreshElement) && refreshElement.ValueKind == JsonValueKind.String
                ? refreshElement.GetString()
                : tokens.RefreshToken;

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number
                && expires.TryGetDouble(out var seconds) && seconds > 0)
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);

            var scope = root.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()
                : tokens.Scope;

            var updated = tokens with { AccessToken = access.GetString()!, RefreshToken = refresh, ExpiresAtUtc = expiresAt, Scope = scope };
            await SaveAsync(updated, ct);
            return updated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP OAuth: refresh request failed for {ServerKey}.", _serverKey);
            return null;
        }
    }

    private async Task<McpOAuthTokenEntity?> ReadRowAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbConexy>();
        return await db.McpOAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == _userId && x.ServerKey == _serverKey, ct);
    }

    private Task SaveAsync(McpOAuthTokens tokens, CancellationToken ct) => MutateRowAsync(ct, row =>
        row.Tokens = _protector.Protect(JsonSerializer.Serialize(tokens)));

    private Task ClearTokensAsync(CancellationToken ct) => MutateRowAsync(ct, row => row.Tokens = null);

    private async Task MutateRowAsync(CancellationToken ct, Action<McpOAuthTokenEntity> apply)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbConexy>();
        var row = await db.McpOAuthTokens
            .FirstOrDefaultAsync(x => x.UserId == _userId && x.ServerKey == _serverKey, ct);
        if (row is null) return;
        apply(row);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

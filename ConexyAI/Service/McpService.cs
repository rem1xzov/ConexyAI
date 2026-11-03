using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Service.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// MCP: добавлено 2026-10-04 — удалённые MCP-серверы (Model Context Protocol, Streamable HTTP).
// Клиент говорит JSON-RPC 2.0 поверх HTTP, реестр собирает список инструментов и раздаёт схемы
// агенту под именами mcp__<server>__<tool>.

/// <summary>A configured MCP server, with secrets already resolved from the environment.</summary>
/// <param name="IsUser">True for a personal server of the user (untrusted URL): it goes through the SSRF guard.</param>
/// <param name="TokenProvider">
/// MCP_OAUTH: OAuth-токен пользователя для этого сервера (если статического токена нет и Refresh уже сохранён).
/// </param>
public sealed record McpServerConfig(
    string Id, string Name, string Url, IReadOnlyDictionary<string, string> Headers, int TimeoutSeconds,
    bool IsUser = false, IMcpTokenProvider? TokenProvider = null);

// MCP_OAUTH: сервер ответил 401/403 и требует авторизации. Реестр отдаёт его агенту без инструментов,
// а раннер добавляет ссылку на вход (её строит IMcpOAuthService — ему нужен id пользователя).
public sealed record McpServerAuthRequired(
    string ServerId,
    string ServerName,
    string ServerUrl,
    string? ResourceMetadataUrl,
    string Reason);

/// <summary>One MCP tool, mapped to the function name advertised to the model.</summary>
public sealed record McpToolDescriptor(
    string FunctionName,
    string ServerId,
    string ServerName,
    string ToolName,
    string Description,
    JsonElement InputSchema,
    // SEC_MCP_CONFIRM: вызовы не-read-only инструментов личных серверов идут через подтверждение.
    bool IsUserServer = false,
    bool ReadOnly = false);

public sealed record McpCallResult(bool Success, string Text);

public class McpException : Exception
{
    public McpException(string message) : base(message) { }
}

// MCP_OAUTH: сервер ответил 401/403 и ждёт авторизации. Несёт всё, что нужно для старта flow:
// id/имя/адрес сервера и (если сервер прислал) resource_metadata из WWW-Authenticate.
public sealed class McpAuthorizationRequiredException : McpException
{
    public McpAuthorizationRequiredException(
        string serverId, string serverName, string serverUrl, string? resourceMetadataUrl, string reason)
        : base(reason)
    {
        ServerId = serverId;
        ServerName = serverName;
        ServerUrl = serverUrl;
        ResourceMetadataUrl = resourceMetadataUrl;
        Reason = reason;
    }

    public string ServerId { get; }
    public string ServerName { get; }
    public string ServerUrl { get; }
    public string? ResourceMetadataUrl { get; }
    public string Reason { get; }
}

public interface IMcpRegistry
{
    /// <summary>Servers whose tools are available (for logging/UI).</summary>
    IReadOnlyList<string> ServerNames { get; }

    /// <summary>All tools of all enabled servers, cached for a short TTL.</summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>Maps an advertised function name back to its tool.</summary>
    bool TryResolve(string functionName, out McpToolDescriptor descriptor);

    /// <summary>MCP_OAUTH: серверы, которые ответили 401/403 и ждут авторизации пользователя.</summary>
    IReadOnlyList<McpServerAuthRequired> AuthorizationRequired { get; }

    Task<McpCallResult> CallAsync(McpToolDescriptor descriptor, string argumentsJson, CancellationToken ct = default);
}

public class McpRegistry : IMcpRegistry
{
    private const int MaxTools = 80;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly List<McpServerConfig> _servers;
    private readonly HttpClient _http;
    private readonly HttpClient _userHttp;
    private readonly PublicUrlGuard? _guard;
    private readonly ILogger<McpRegistry> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, McpHttpClient> _clients = new(StringComparer.Ordinal);

    private IReadOnlyList<McpToolDescriptor> _cached = Array.Empty<McpToolDescriptor>();
    private Dictionary<string, McpToolDescriptor> _byFunction = new(StringComparer.Ordinal);
    private IReadOnlyList<McpServerAuthRequired> _authRequired = Array.Empty<McpServerAuthRequired>();
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public McpRegistry(
        IReadOnlyList<McpServerConfig> servers, HttpClient http, ILogger<McpRegistry> logger,
        HttpClient? userHttp = null, PublicUrlGuard? guard = null)
    {
        _logger = logger;
        _http = http;
        _userHttp = userHttp ?? http;
        _guard = guard;
        _servers = servers.ToList();
    }

    public IReadOnlyList<string> ServerNames => _servers.Select(s => s.Name).ToList();

    public IReadOnlyList<McpServerAuthRequired> AuthorizationRequired
    {
        get { lock (_lock) { return _authRequired; } }
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default)
    {
        if (_cached.Count > 0 && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        var list = new List<McpToolDescriptor>();
        var map = new Dictionary<string, McpToolDescriptor>(StringComparer.Ordinal);
        var authRequired = new List<McpServerAuthRequired>();

        foreach (var server in _servers)
        {
            if (list.Count >= MaxTools) break;
            try
            {
                var client = GetClient(server);
                var tools = await client.ListToolsAsync(ct);
                foreach (var tool in tools)
                {
                    var functionName = BuildFunctionName(server, tool.ToolName, map);
                    var descriptor = new McpToolDescriptor(
                        functionName, server.Id, server.Name, tool.ToolName, Truncate(tool.Description, 400), tool.InputSchema,
                        server.IsUser, tool.ReadOnly);
                    list.Add(descriptor);
                    map[functionName] = descriptor;
                    if (list.Count >= MaxTools) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (McpAuthorizationRequiredException ex)
            {
                // MCP_OAUTH: инструментов у сервера нет, но агент должен сказать пользователю, куда войти.
                authRequired.Add(new McpServerAuthRequired(
                    ex.ServerId, ex.ServerName, ex.ServerUrl, ex.ResourceMetadataUrl, ex.Reason));
                _logger.LogInformation(
                    "MCP server '{Server}' requires authorization (server key hidden).", server.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server '{Server}' tool listing failed", server.Name);
            }
        }

        // PREFIX_CACHE: порядок MCP-инструментов не должен зависеть от того, в каком порядке их вернули
        // серверы. Схемы идут в префиксе запроса, и «дрожащий» порядок между прогонами ломает кэш
        // префикса (тогда те же схемы оплачиваются заново). Сортируем по имени инструмента.
        list.Sort((a, b) => string.CompareOrdinal(a.FunctionName, b.FunctionName));

        lock (_lock)
        {
            _cached = list;
            _byFunction = map;
            _authRequired = authRequired;
            _cachedAt = DateTimeOffset.UtcNow;
        }
        return list;
    }

    public bool TryResolve(string functionName, out McpToolDescriptor descriptor)
    {
        lock (_lock)
        {
            return _byFunction.TryGetValue(functionName, out descriptor!);
        }
    }

    public async Task<McpCallResult> CallAsync(McpToolDescriptor descriptor, string argumentsJson, CancellationToken ct = default)
    {
        var server = _servers.FirstOrDefault(s => s.Id == descriptor.ServerId);
        if (server is null)
            return new McpCallResult(false, $"MCP server '{descriptor.ServerId}' is not configured.");

        try
        {
            var client = GetClient(server);
            return await client.CallToolAsync(descriptor.ToolName, argumentsJson, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpAuthorizationRequiredException ex)
        {
            // MCP_OAUTH: токен истёк или отозван уже во время прогона — инструмент больше не работает.
            RememberAuthRequired(new McpServerAuthRequired(
                ex.ServerId, ex.ServerName, ex.ServerUrl, ex.ResourceMetadataUrl, ex.Reason));
            return new McpCallResult(false,
                $"MCP-сервер «{descriptor.ServerName}» требует авторизации в ConexyAI, доступ больше не действует.");
        }
        catch (Exception ex)
        {
            return new McpCallResult(false, $"MCP call failed: {ex.Message}");
        }
    }

    private void RememberAuthRequired(McpServerAuthRequired required)
    {
        lock (_lock)
        {
            var items = new List<McpServerAuthRequired>(_authRequired);
            items.RemoveAll(x => string.Equals(x.ServerId, required.ServerId, StringComparison.Ordinal));
            items.Add(required);
            _authRequired = items;
        }
    }

    // One client per server keeps the MCP session (Mcp-Session-Id) alive across list/call.
    private McpHttpClient GetClient(McpServerConfig server)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(server.Id, out var existing)) return existing;
            // SEC_MCP_SSRF: личный сервер пользователя ходит только через клиент с пиннингом публичного IP.
            var client = server.IsUser
                ? new McpHttpClient(server, _userHttp, _logger, _guard, server.TokenProvider)
                : new McpHttpClient(server, _http, _logger, null, server.TokenProvider);
            _clients[server.Id] = client;
            return client;
        }
    }

    // Keeps function names within ^[a-zA-Z0-9_-]{1,64}$ that the OpenAI/DeepSeek tool schema requires.
    private static string BuildFunctionName(McpServerConfig server, string toolName, Dictionary<string, McpToolDescriptor> taken)
    {
        var baseName = $"mcp__{Sanitize(server.Id)}__{Sanitize(toolName)}";
        if (baseName.Length > 64) baseName = baseName[..64];
        var name = baseName;
        var suffix = 2;
        while (taken.ContainsKey(name))
        {
            var tail = "_" + suffix++;
            name = baseName.Length + tail.Length > 64 ? baseName[..(64 - tail.Length)] + tail : baseName + tail;
        }
        return name;
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.Length == 0 ? "srv" : sb.ToString();
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";
}

// MCP: фабрика создаёт реестр на один прогон из операторских серверов (appsettings) и
// ЛИЧНЫХ серверов пользователя (приходят с запросом, в БД не хранятся). Личный сервер с тем же id
// перекрывает операторский — так агент работает под аккаунтом пользователя, а не под общим.
public interface IMcpRegistryFactory
{
    /// <param name="userServers">Личные серверы пользователя из запроса/БД.</param>
    /// <param name="tokenProvider">
    /// MCP_OAUTH: фабрика OAuth-провайдера по ключу сервера (хост+путь). Возвращает null, когда
    /// у пользователя нет сохранённой авторизации этого сервера.
    /// </param>
    IMcpRegistry Create(
        IReadOnlyList<McpServerInput>? userServers,
        Func<string, IMcpTokenProvider?>? tokenProvider = null);
}

public class McpRegistryFactory : IMcpRegistryFactory
{
    private const int MaxServers = 10;

    /// <summary>HttpClient for operator servers (trusted config, may live on the internal network).</summary>
    public const string OperatorHttpClientName = "mcp";

    /// <summary>HttpClient for user servers: no redirects, connects only to validated public addresses.</summary>
    public const string UserHttpClientName = "mcp-user";

    private readonly List<McpServerConfig> _operatorServers;
    private readonly HttpClient _http;
    private readonly HttpClient _userHttp;
    private readonly PublicUrlGuard _guard;
    private readonly ILogger<McpRegistry> _logger;

    public McpRegistryFactory(
        IOptions<McpOptions> options, IHttpClientFactory httpClientFactory, ILogger<McpRegistry> logger,
        PublicUrlGuard? guard = null)
    {
        _logger = logger;
        _http = httpClientFactory.CreateClient(OperatorHttpClientName);
        _userHttp = httpClientFactory.CreateClient(UserHttpClientName);
        _guard = guard ?? new PublicUrlGuard(new DnsHostAddressResolver());
        _operatorServers = ResolveOperatorServers(options.Value);
    }

    /// <summary>
    /// SEC_MCP_SSRF: primary handler of <see cref="UserHttpClientName"/>. Redirects are off (a public
    /// server could 302 to docker-socket-proxy) and the socket is opened only to an address that passed
    /// <see cref="PublicUrlGuard"/> at connect time (DNS rebinding).
    /// </summary>
    public static SocketsHttpHandler CreateUserHandler(PublicUrlGuard guard, IWebProxy? proxy = null) => new()
    {
        AllowAutoRedirect = false,
        // AGENT_PROXY: с прокси соединение открывает он (и до внутренних адресов не достанет), поэтому
        // пиннинг сокета не нужен. Preflight-проверка адреса в McpHttpClient остаётся.
        UseProxy = proxy is not null,
        Proxy = proxy,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = proxy is null
            ? (context, ct) => WebPageFetcher.ConnectPinnedAsync(guard, context.DnsEndPoint, ct)
            : null,
    };

    /// <summary>AGENT_PROXY: тот же исходящий прокси, что у чтения страниц, — для MCP-серверов.</summary>
    public static SocketsHttpHandler CreateProxiedHandler(IWebProxy proxy) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = true,
        Proxy = proxy,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>
    /// SEC_MCP_SSRF: validates a user MCP URL (scheme, credentials, every resolved address public).
    /// Returns null when the URL is acceptable, otherwise a human-readable reason.
    /// </summary>
    public static async Task<string?> ValidateUserUrlAsync(PublicUrlGuard guard, string? url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
            return "URL MCP-сервера должен быть абсолютным (https://…).";
        var check = await guard.CheckAsync(uri, ct);
        return check.Allowed ? null : check.Error ?? "Адрес MCP-сервера не публичный.";
    }

    public IMcpRegistry Create(
        IReadOnlyList<McpServerInput>? userServers,
        Func<string, IMcpTokenProvider?>? tokenProvider = null)
    {
        var merged = new List<McpServerConfig>(_operatorServers);
        foreach (var input in userServers ?? Array.Empty<McpServerInput>())
        {
            var server = ResolveUserServer(input, tokenProvider);
            if (server is null) continue;
            merged.RemoveAll(s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
            merged.Add(server);
        }
        if (merged.Count > MaxServers) merged = merged.Take(MaxServers).ToList();
        return new McpRegistry(merged, _http, _logger, _userHttp, _guard);
    }

    private static List<McpServerConfig> ResolveOperatorServers(McpOptions options)
    {
        var servers = new List<McpServerConfig>();
        foreach (var configured in options.Servers)
        {
            if (!configured.Enabled) continue;

            var url = Expand(configured.Url);
            if (!IsHttpUrl(url)) continue;

            var id = string.IsNullOrWhiteSpace(configured.Id) ? Sanitize(configured.Name) : configured.Id.Trim();
            var name = string.IsNullOrWhiteSpace(configured.Name) ? id : configured.Name.Trim();
            var headers = configured.Headers.ToDictionary(k => k.Key, v => Expand(v.Value), StringComparer.OrdinalIgnoreCase);

            servers.Add(new McpServerConfig(id, name, url, headers, configured.TimeoutSeconds <= 0 ? 30 : configured.TimeoutSeconds));
            if (servers.Count >= MaxServers) break;
        }
        return servers;
    }

    // A user server carries its own access token; it is sent as "Authorization: Bearer <token>".
    // MCP_OAUTH: статический токен из настроек имеет приоритет; без него подключается OAuth-провайдер.
    private static McpServerConfig? ResolveUserServer(
        McpServerInput input, Func<string, IMcpTokenProvider?>? tokenProvider)
    {
        if (input is null || !input.Enabled) return null;
        var url = (input.Url ?? string.Empty).Trim();
        if (!IsHttpUrl(url)) return null;

        var id = !string.IsNullOrWhiteSpace(input.Id)
            ? Sanitize(input.Id!)
            : !string.IsNullOrWhiteSpace(input.Name) ? Sanitize(input.Name!) : Sanitize(new Uri(url).Host);
        var name = string.IsNullOrWhiteSpace(input.Name) ? id : input.Name!.Trim();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(input.Token))
            headers["Authorization"] = "Bearer " + input.Token!.Trim();

        IMcpTokenProvider? provider = null;
        if (!headers.ContainsKey("Authorization") && tokenProvider is not null)
            provider = tokenProvider(McpServerKey.FromUrl(url));

        return new McpServerConfig(id, name, url, headers, 30, IsUser: true, TokenProvider: provider);
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.Length == 0 ? "srv" : sb.ToString();
    }

    // ${VAR} -> environment, so operator secrets never live in appsettings.json.
    private static string Expand(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal)) return value;
        var result = value;
        var start = result.IndexOf("${", StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = result.IndexOf('}', start);
            if (end < 0) break;
            var name = result[(start + 2)..end];
            var replacement = Environment.GetEnvironmentVariable(name) ?? string.Empty;
            result = result[..start] + replacement + result[(end + 1)..];
            start = result.IndexOf("${", StringComparison.Ordinal);
        }
        return result;
    }
}

/// <summary>JSON-RPC 2.0 client for one MCP server over Streamable HTTP.</summary>
public class McpHttpClient
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly McpServerConfig _server;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly PublicUrlGuard? _guard;
    private readonly IMcpTokenProvider? _tokenProvider;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _sessionId;
    private bool _initialized;
    private int _nextId;

    public McpHttpClient(
        McpServerConfig server, HttpClient http, ILogger logger, PublicUrlGuard? guard = null,
        IMcpTokenProvider? tokenProvider = null)
    {
        _server = server;
        _http = http;
        _logger = logger;
        _guard = guard;
        _tokenProvider = tokenProvider;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var result = await SendAsync("tools/list", "{}", ct);
        var tools = new List<McpToolDescriptor>();
        if (result is not { } element || !element.TryGetProperty("tools", out var array) || array.ValueKind != JsonValueKind.Array)
            return tools;

        foreach (var tool in array.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var schema = tool.TryGetProperty("inputSchema", out var s) ? s.Clone() : EmptyObject();
            // MCP annotations.readOnlyHint — сервер сам объявляет инструмент только читающим.
            var readOnly = tool.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True;
            tools.Add(new McpToolDescriptor(string.Empty, _server.Id, _server.Name, name!, description, schema, _server.IsUser, readOnly));
        }
        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var arguments = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        var parameters = $"{{\"name\":{JsonSerializer.Serialize(toolName)},\"arguments\":{arguments}}}";
        var result = await SendAsync("tools/call", parameters, ct);

        if (result is not { } element)
            return new McpCallResult(false, "MCP server returned no result.");

        var isError = element.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
        var text = ExtractText(element);
        if (string.IsNullOrWhiteSpace(text))
            text = element.GetRawText();
        return new McpCallResult(!isError, text);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            var parameters =
                $"{{\"protocolVersion\":\"{ProtocolVersion}\",\"capabilities\":{{}},\"clientInfo\":{{\"name\":\"ConexyAI\",\"version\":\"1.0\"}}}}";
            await SendAsync("initialize", parameters, ct, captureSession: true);

            // Best-effort notification: the server may answer 202 with no body.
            try { await SendAsync("notifications/initialized", "{}", ct, expectResult: false); }
            catch (Exception ex) { _logger.LogDebug(ex, "MCP initialized notification failed for {Server}", _server.Name); }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<JsonElement?> SendAsync(string method, string paramsJson, CancellationToken ct, bool expectResult = true, bool captureSession = false)
    {
        // SEC_MCP_SSRF: URL пользователя перепроверяется перед КАЖДЫМ запросом (сохранённый адрес мог
        // начать резолвиться во внутреннюю сеть). Окончательная защита — пиннинг в ConnectCallback.
        if (_server.IsUser)
        {
            if (_guard is null)
                throw new McpException("MCP server URL guard is not configured.");
            var error = await McpRegistryFactory.ValidateUserUrlAsync(_guard, _server.Url, ct);
            if (error is not null)
                throw new McpException("MCP server URL rejected: " + error);
        }

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonNode.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson) ?? new JsonObject(),
        };
        var body = request.ToJsonString();

        // MCP_OAUTH: при 401 с сохранённым OAuth-токеном один раз повторяем запрос с refresh.
        var attempt = 0;
        while (true)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _server.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!string.IsNullOrEmpty(_sessionId))
                message.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            foreach (var (key, value) in _server.Headers)
                message.Headers.TryAddWithoutValidation(key, value);

            // Статический Authorization из настроек имеет приоритет; OAuth-токен — только без него.
            var oauthToken = _tokenProvider is not null && !_server.Headers.ContainsKey("Authorization")
                ? await _tokenProvider.GetAccessTokenAsync(forceRefresh: attempt > 0, ct)
                : null;
            if (!string.IsNullOrEmpty(oauthToken))
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + oauthToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_server.TimeoutSeconds));

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (captureSession && response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
                _sessionId = sessionValues.FirstOrDefault();

            var unauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (unauthorized && _server.IsUser)
            {
                // Повторяем только если на первом заходе был токен: без токена повторять нечего.
                if (attempt == 0 && _tokenProvider is not null && !string.IsNullOrEmpty(oauthToken))
                {
                    attempt++;
                    _logger.LogInformation(
                        "MCP server '{Server}' refused the OAuth token; retrying after refresh.", _server.Name);
                    continue;
                }

                // MCP_OAUTH: структурный результат вместо простой ошибки: выше реестр превратит его
                // в «сервер требует авторизации» и агент покажет пользователю ссылку на вход.
                var resourceMetadata = ParseResourceMetadataUrl(BuildWwwAuthenticate(response.Headers));
                var reason = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Сервер требует авторизации (HTTP 401)."
                    : "Сервер отклонил доступ (HTTP 403).";
                throw new McpAuthorizationRequiredException(
                    _server.Id, _server.Name, _server.Url, resourceMetadata, reason);
            }

            // SEC_MCP_SSRF: тело неуспешного ответа модели не отдаём — только статус. Иначе ошибка любого
            // внутреннего сервиса превращалась бы в канал чтения. Редирект (3xx) тоже считается ошибкой.
            if (!response.IsSuccessStatusCode)
                throw new McpException($"HTTP {(int)response.StatusCode}");

            if (!expectResult)
                return null;

            var raw = await SafeReadAsync(response, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return ParseResponse(raw, contentType, id);
        }
    }

    private static JsonElement? ParseResponse(string body, string contentType, int id)
    {
        var messages = new List<JsonElement>();
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith("event:", StringComparison.Ordinal) ||
            body.Contains("\ndata:", StringComparison.Ordinal))
        {
            foreach (var block in body.Split("\n\n"))
            {
                foreach (var line in block.Split('\n'))
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var payload = line[5..].Trim();
                    if (payload.Length == 0 || payload == "[DONE]") continue;
                    try { messages.Add(JsonDocument.Parse(payload).RootElement.Clone()); }
                    catch (JsonException) { /* ignore keep-alive or partial frames */ }
                }
            }
        }

        if (messages.Count == 0)
            messages.Add(JsonDocument.Parse(body).RootElement.Clone());

        foreach (var message in messages)
        {
            if (message.TryGetProperty("id", out var messageId) &&
                messageId.ValueKind == JsonValueKind.Number &&
                messageId.GetInt32() == id)
            {
                if (message.TryGetProperty("error", out var error))
                    throw new McpException(error.GetRawText());
                return message.TryGetProperty("result", out var result) ? result.Clone() : null;
            }
        }

        return null;
    }

    private static string ExtractText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                item.TryGetProperty("text", out var text))
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }
        return string.Join("\n", parts);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// MCP_OAUTH: достаёт <c>resource_metadata</c> из заголовка WWW-Authenticate (RFC 9728).
    /// Возвращает null для простого <c>Bearer</c> или если значение не абсолютный адрес.
    /// </summary>
    public static string? ParseResourceMetadataUrl(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        // Заголовок может содержать несколько challenges через запятую; параметр ищется в любом.
        foreach (var part in header.Split(','))
        {
            var index = part.IndexOf("resource_metadata", StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            var rest = part[(index + "resource_metadata".Length)..].TrimStart();
            if (!rest.StartsWith('=')) continue;
            rest = rest[1..].TrimStart();

            string value;
            if (rest.StartsWith('"'))
            {
                var end = rest.IndexOf('"', 1);
                if (end <= 1) continue;
                value = rest[1..end];
            }
            else
            {
                var end = rest.IndexOfAny(new[] { ' ', ';', ',' });
                value = end < 0 ? rest : rest[..end];
            }

            value = value.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.ToString();
        }

        return null;
    }

    private static string BuildWwwAuthenticate(HttpResponseHeaders headers) =>
        string.Join(", ", headers.WwwAuthenticate.Select(header =>
            string.IsNullOrWhiteSpace(header.Parameter) ? header.Scheme : header.Scheme + " " + header.Parameter));
}

using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using ConexyAI.Service;
using ConexyAI.Service.Web;
using Microsoft.Extensions.Logging.Abstractions;

// MCP_OAUTH: добавлено 2026-11-03 — тесты разбора WWW-Authenticate, PKCE, TTL/одноразовости state
// и отказа от непубличных (внутренних) OAuth-эндпоинтов. Все тесты без БД и без сети.
internal static class McpOAuthTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("mcp oauth: resource_metadata is parsed from WWW-Authenticate", ResourceMetadataParsingAsync);
        TestRegistry.Add("mcp oauth: PKCE S256 challenge matches the RFC vector", PkceAsync);
        TestRegistry.Add("mcp oauth: pending state is single-use and expires after 10 minutes", StateStoreAsync);
        TestRegistry.Add("mcp oauth: private discovery endpoints are refused", PrivateEndpointRefusedAsync);
        TestRegistry.Add("mcp oauth: discovery honours the resource metadata and drops a private registration endpoint", DiscoveryParsingAsync);
        TestRegistry.Add("mcp oauth: a 401 produces a structured authorization-required result", UnauthorizedMcpServerAsync);
        TestRegistry.Add("mcp oauth: the token provider is attached to a user server without a static token", TokenProviderAttachedAsync);
        TestRegistry.Add("mcp oauth: server key is the normalised host and path", ServerKeyAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task ResourceMetadataParsingAsync()
    {
        Assert(
            McpHttpClient.ParseResourceMetadataUrl(
                "Bearer resource_metadata=\"https://mcp.notion.com/.well-known/oauth-protected-resource\"")
            == "https://mcp.notion.com/.well-known/oauth-protected-resource",
            "a quoted resource_metadata is extracted");

        Assert(
            McpHttpClient.ParseResourceMetadataUrl("Bearer resource_metadata=https://auth.acme.io/meta, Basic realm=\"x\"")
            == "https://auth.acme.io/meta",
            "an unquoted resource_metadata is extracted");

        Assert(
            McpHttpClient.ParseResourceMetadataUrl("Basic realm=\"a\", Bearer resource_metadata=\"https://as.acme.io/meta\"")
            == "https://as.acme.io/meta",
            "the parameter is found in any challenge of the header");

        Assert(McpHttpClient.ParseResourceMetadataUrl("Bearer") is null, "plain Bearer has no metadata URL");
        Assert(McpHttpClient.ParseResourceMetadataUrl("Bearer realm=\"mcp\"") is null, "other parameters are ignored");
        Assert(McpHttpClient.ParseResourceMetadataUrl("Bearer resource_metadata=\"not-a-url\"") is null, "a relative value is ignored");
        Assert(McpHttpClient.ParseResourceMetadataUrl(null) is null, "no header, no URL");
        await Task.CompletedTask;
    }

    private static Task PkceAsync()
    {
        // RFC 7636, Appendix B.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert(McpPkce.CreateChallenge(verifier) == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            "the S256 challenge must match the RFC vector");

        var generated = McpPkce.CreateVerifier();
        Assert(generated.Length == 43, "a 32-byte verifier is 43 base64url characters, got " + generated.Length);
        Assert(generated.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'), "verifier is base64url, got " + generated);
        Assert(McpPkce.CreateChallenge(generated) != McpPkce.CreateChallenge(verifier), "challenges differ for different verifiers");
        return Task.CompletedTask;
    }

    private static Task StateStoreAsync()
    {
        var time = new ManualTime();
        var store = new McpOAuthStateStore(time);
        var userId = Guid.NewGuid();

        McpPendingAuthorization New(string state, DateTimeOffset created) => new(
            state, userId, "notion", "Notion", "https://mcp.acme.io/mcp", "mcp.acme.io/mcp",
            "verifier", "client-1", "https://auth.acme.io/token", "https://mcp.acme.io/mcp", created);

        store.Create(New("state-1", time.GetUtcNow()));
        Assert(!store.TryTake("unknown", out _), "an unknown state is refused");
        Assert(store.TryTake("state-1", out var taken) && taken.ServerKey == "mcp.acme.io/mcp", "the pending authorization is returned");
        Assert(!store.TryTake("state-1", out _), "state is single-use");

        store.Create(New("state-2", time.GetUtcNow()));
        time.Now = time.Now.AddMinutes(11);
        Assert(!store.TryTake("state-2", out _), "state expires after 10 minutes");

        store.Create(New("state-3", time.GetUtcNow()));
        time.Now = time.Now.AddMinutes(9);
        Assert(store.TryTake("state-3", out _), "state is still usable before the TTL");

        Assert(!store.TryTake("", out _), "an empty state is refused");
        return Task.CompletedTask;
    }

    private static async Task PrivateEndpointRefusedAsync()
    {
        var guard = PublicGuard();

        foreach (var url in new[]
                 {
                     "http://127.0.0.1:8080/.well-known/oauth-protected-resource",
                     "http://169.254.169.254/latest/meta-data",
                     "https://rebind.attacker.com/.well-known/oauth-authorization-server",
                     "file:///etc/passwd",
                 })
        {
            Assert(await McpOAuthDiscovery.ValidateEndpointAsync(guard, url) is not null,
                "a non-public OAuth endpoint must be refused: " + url);
        }

        Assert(await McpOAuthDiscovery.ValidateEndpointAsync(guard, "https://mcp.acme.io/meta") is null,
            "a public OAuth endpoint is allowed");

        // Заголовок указывает на внутренний адрес: туда не должно уйти ни одного запроса.
        var handler = new RoutedHandler(new Dictionary<string, string>());
        var discovery = new McpOAuthDiscovery(new HttpClient(handler), guard, NullLogger<McpOAuthDiscovery>.Instance);
        var result = await discovery.DiscoverAsync(
            "https://mcp.acme.io/mcp", "http://127.0.0.1:9000/.well-known/oauth-protected-resource");

        Assert(!result.Success, "discovery fails when nothing answers");
        Assert(handler.Urls.All(u => !u.Contains("127.0.0.1")), "no request may reach a private endpoint: " + string.Join(",", handler.Urls));
        Assert(handler.Urls.Count > 0 && handler.Urls.All(u => u.Contains("mcp.acme.io")),
            "only public candidates are requested: " + string.Join(",", handler.Urls));
    }

    private static async Task DiscoveryParsingAsync()
    {
        var handler = new RoutedHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://mcp.acme.io/.well-known/oauth-protected-resource/mcp"] =
                "{\"resource\":\"https://mcp.acme.io/mcp\",\"authorization_servers\":[\"https://auth.acme.io\"],\"scopes_supported\":[\"mcp:read\",\"mcp:write\"]}",
            ["https://auth.acme.io/.well-known/oauth-authorization-server"] =
                "{\"authorization_endpoint\":\"https://auth.acme.io/authorize\",\"token_endpoint\":\"https://auth.acme.io/token\",\"registration_endpoint\":\"http://127.0.0.1:9000/register\"}",
        });

        var discovery = new McpOAuthDiscovery(new HttpClient(handler), PublicGuard(), NullLogger<McpOAuthDiscovery>.Instance);
        var result = await discovery.DiscoverAsync("https://mcp.acme.io/mcp", null);

        Assert(result.Success, "discovery succeeds: " + result.Error);
        Assert(result.AuthorizationEndpoint == "https://auth.acme.io/authorize", "authorization endpoint is parsed");
        Assert(result.TokenEndpoint == "https://auth.acme.io/token", "token endpoint is parsed");
        Assert(result.Resource == "https://mcp.acme.io/mcp", "the resource identifier comes from RFC 9728 metadata");
        Assert(result.Scope == "mcp:read mcp:write", "resource scopes are requested");
        Assert(result.RegistrationEndpoint is null, "a private registration_endpoint must be dropped");
    }

    private static async Task UnauthorizedMcpServerAsync()
    {
        var handler = new UnauthorizedHandler();
        var registry = new McpRegistry(
            new[]
            {
                new McpServerConfig("notion", "Notion", "https://mcp.acme.io/mcp", new Dictionary<string, string>(), 5, IsUser: true),
            },
            new HttpClient(handler), NullLogger<McpRegistry>.Instance, guard: PublicGuard());

        var tools = await registry.ListToolsAsync();
        Assert(tools.Count == 0, "a server that needs authorization exposes no tools");

        var required = registry.AuthorizationRequired;
        Assert(required.Count == 1, "the server is reported as requiring authorization, got " + required.Count);
        Assert(required[0].ServerId == "notion" && required[0].ServerName == "Notion", "server identity is carried");
        Assert(required[0].ResourceMetadataUrl == "https://mcp.acme.io/.well-known/oauth-protected-resource",
            "resource_metadata from WWW-Authenticate is carried: " + required[0].ResourceMetadataUrl);
    }

    private static async Task TokenProviderAttachedAsync()
    {
        var handler = new McpHandler();
        var guard = PublicGuard();
        var factory = new McpRegistryFactory(
            Microsoft.Extensions.Options.Options.Create(new ConexyAI.Configuration.McpOptions()),
            new FakeHttpClientFactory(handler), NullLogger<McpRegistry>.Instance, guard);

        string? seenKey = null;
        var registry = factory.Create(
            new List<ConexyAI.Contract.McpServerInput>
            {
                new() { Id = "notion", Name = "Notion", Url = "https://mcp.acme.io/mcp" },
            },
            key =>
            {
                seenKey = key;
                return new FakeTokenProvider("oauth-access-token");
            });

        var tools = await registry.ListToolsAsync();
        Assert(tools.Count == 1, "the OAuth-authorized server lists tools");
        Assert(seenKey == "mcp.acme.io/mcp", "the provider is keyed by the normalised server address: " + seenKey);
        Assert(handler.Auths.Contains("Bearer oauth-access-token"), "the OAuth access token is sent: " + string.Join(",", handler.Auths));

        // MCP_OAUTH_PRIORITY: статический токен из настроек имеет приоритет над OAuth-провайдером.
        handler.Auths.Clear();
        string? secondKey = null;
        var withStatic = factory.Create(
            new List<ConexyAI.Contract.McpServerInput>
            {
                new() { Id = "notion", Name = "Notion", Url = "https://mcp.acme.io/mcp", Token = "static-token" },
            },
            key =>
            {
                secondKey = key;
                return new FakeTokenProvider("oauth-access-token");
            });

        await withStatic.ListToolsAsync();
        Assert(secondKey is null, "the provider is not used when a static token is configured");
        Assert(handler.Auths.Contains("Bearer static-token") && !handler.Auths.Contains("Bearer oauth-access-token"),
            "the static token is sent: " + string.Join(",", handler.Auths));
    }

    private static Task ServerKeyAsync()
    {
        Assert(McpServerKey.FromUrl("https://MCP.Acme.io/mcp/") == "mcp.acme.io/mcp", "host is lower-cased and the trailing slash dropped");
        Assert(McpServerKey.FromUrl("https://MCP.Acme.io:8443/mcp") == "mcp.acme.io:8443/mcp", "a non-default port is kept");
        Assert(McpServerKey.FromUrl("not a url") == string.Empty, "an invalid URL has an empty key");
        return Task.CompletedTask;
    }

    private static PublicUrlGuard PublicGuard() => new(new MapResolver(new Dictionary<string, string[]>
    {
        ["mcp.acme.io"] = new[] { "93.184.216.34" },
        ["auth.acme.io"] = new[] { "93.184.216.35" },
        ["rebind.attacker.com"] = new[] { "172.18.0.5" },
    }));

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MapResolver : IHostAddressResolver
    {
        private readonly Dictionary<string, string[]> _map;
        public MapResolver(Dictionary<string, string[]> map) => _map = map;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
            Task.FromResult(_map.TryGetValue(host, out var addresses)
                ? addresses.Select(IPAddress.Parse).ToArray()
                : Array.Empty<IPAddress>());
    }

    private sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes;
        public List<string> Urls { get; } = new();

        public RoutedHandler(Dictionary<string, string> routes) => _routes = routes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (!_routes.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.TryAddWithoutValidation(
                "WWW-Authenticate",
                "Bearer resource_metadata=\"https://mcp.acme.io/.well-known/oauth-protected-resource\"");
            return Task.FromResult(response);
        }
    }

    private sealed class FakeTokenProvider : IMcpTokenProvider
    {
        private readonly string _token;
        public FakeTokenProvider(string token) => _token = token;
        public Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default) =>
            Task.FromResult<string?>(_token);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    // Минимальный фейк MCP-сервера: handshake + tools/list с одним инструментом.
    private sealed class McpHandler : HttpMessageHandler
    {
        public List<string> Auths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.TryGetValues("Authorization", out var auth)) Auths.Add(string.Join(",", auth));

            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var method = document.RootElement.GetProperty("method").GetString();

            if (method == "notifications/initialized")
                return new HttpResponseMessage(HttpStatusCode.Accepted);

            var id = document.RootElement.GetProperty("id").GetInt32();
            var result = method == "initialize"
                ? "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}"
                : "{\"tools\":[{\"name\":\"search\",\"description\":\"Search\",\"inputSchema\":{\"type\":\"object\"}}]}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConexyAI.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// MCP: добавлено 2026-10-04 — удалённые MCP-серверы (Model Context Protocol, Streamable HTTP).
// Клиент говорит JSON-RPC 2.0 поверх HTTP, реестр собирает список инструментов и раздаёт схемы
// агенту под именами mcp__<server>__<tool>.

/// <summary>A configured MCP server, with secrets already resolved from the environment.</summary>
public sealed record McpServerConfig(string Id, string Name, string Url, IReadOnlyDictionary<string, string> Headers, int TimeoutSeconds);

/// <summary>One MCP tool, mapped to the function name advertised to the model.</summary>
public sealed record McpToolDescriptor(
    string FunctionName,
    string ServerId,
    string ServerName,
    string ToolName,
    string Description,
    JsonElement InputSchema);

public sealed record McpCallResult(bool Success, string Text);

public sealed class McpException : Exception
{
    public McpException(string message) : base(message) { }
}

public interface IMcpRegistry
{
    /// <summary>Servers whose tools are available (for logging/UI).</summary>
    IReadOnlyList<string> ServerNames { get; }

    /// <summary>All tools of all enabled servers, cached for a short TTL.</summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>Maps an advertised function name back to its tool.</summary>
    bool TryResolve(string functionName, out McpToolDescriptor descriptor);

    Task<McpCallResult> CallAsync(McpToolDescriptor descriptor, string argumentsJson, CancellationToken ct = default);
}

public class McpRegistry : IMcpRegistry
{
    private const int MaxServers = 10;
    private const int MaxTools = 80;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly List<McpServerConfig> _servers;
    private readonly HttpClient _http;
    private readonly ILogger<McpRegistry> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, McpHttpClient> _clients = new(StringComparer.Ordinal);

    private IReadOnlyList<McpToolDescriptor> _cached = Array.Empty<McpToolDescriptor>();
    private Dictionary<string, McpToolDescriptor> _byFunction = new(StringComparer.Ordinal);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public McpRegistry(IOptions<McpOptions> options, IHttpClientFactory httpClientFactory, ILogger<McpRegistry> logger)
    {
        _logger = logger;
        _http = httpClientFactory.CreateClient("mcp");
        _servers = ResolveServers(options.Value);
    }

    public IReadOnlyList<string> ServerNames => _servers.Select(s => s.Name).ToList();

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default)
    {
        if (_cached.Count > 0 && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        var list = new List<McpToolDescriptor>();
        var map = new Dictionary<string, McpToolDescriptor>(StringComparer.Ordinal);

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
                        functionName, server.Id, server.Name, tool.ToolName, Truncate(tool.Description, 400), tool.InputSchema);
                    list.Add(descriptor);
                    map[functionName] = descriptor;
                    if (list.Count >= MaxTools) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP server '{Server}' tool listing failed", server.Name);
            }
        }

        lock (_lock)
        {
            _cached = list;
            _byFunction = map;
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
        catch (Exception ex)
        {
            return new McpCallResult(false, $"MCP call failed: {ex.Message}");
        }
    }

    // One client per server keeps the MCP session (Mcp-Session-Id) alive across list/call.
    private McpHttpClient GetClient(McpServerConfig server)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(server.Id, out var existing)) return existing;
            var client = new McpHttpClient(server, _http, _logger);
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

    private static List<McpServerConfig> ResolveServers(McpOptions options)
    {
        var servers = new List<McpServerConfig>();
        foreach (var configured in options.Servers)
        {
            if (!configured.Enabled) continue;

            var url = Expand(configured.Url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(configured.Id) ? Sanitize(configured.Name) : configured.Id.Trim();
            var name = string.IsNullOrWhiteSpace(configured.Name) ? id : configured.Name.Trim();
            var headers = configured.Headers.ToDictionary(k => k.Key, v => Expand(v.Value), StringComparer.OrdinalIgnoreCase);

            servers.Add(new McpServerConfig(id, name, uri.ToString(), headers, configured.TimeoutSeconds <= 0 ? 30 : configured.TimeoutSeconds));
            if (servers.Count >= MaxServers) break;
        }
        return servers;
    }

    // ${VAR} -> environment, so secrets never live in appsettings.json.
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _sessionId;
    private bool _initialized;
    private int _nextId;

    public McpHttpClient(McpServerConfig server, HttpClient http, ILogger logger)
    {
        _server = server;
        _http = http;
        _logger = logger;
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
            tools.Add(new McpToolDescriptor(string.Empty, _server.Id, _server.Name, name!, description, schema));
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
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonNode.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson) ?? new JsonObject(),
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _server.Url)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(_sessionId))
            message.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        foreach (var (key, value) in _server.Headers)
            message.Headers.TryAddWithoutValidation(key, value);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_server.TimeoutSeconds));

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

        if (captureSession && response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
            _sessionId = sessionValues.FirstOrDefault();

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, timeoutCts.Token);
            throw new McpException($"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        if (!expectResult)
            return null;

        var raw = await SafeReadAsync(response, timeoutCts.Token);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        return ParseResponse(raw, contentType, id);
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

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

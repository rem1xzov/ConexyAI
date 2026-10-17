using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop.Mcp;

/// <summary>
/// Все инструменты, которые десктоп предлагает агенту сверх файлов и терминала: встроенный Google и
/// локальные MCP-серверы из mcp.json. Живёт весь сеанс (процессы MCP не перезапускаются на каждый ход).
/// </summary>
public sealed class LocalTools : IAsyncDisposable
{
    private const int MaxOutputChars = 60_000;

    private readonly Dictionary<string, StdioMcpClient> _servers = new(StringComparer.Ordinal);
    private readonly ICommandApprover _approver;

    public LocalTools(ICommandApprover approver)
    {
        _approver = approver;
        Google = new GoogleConnector(approver);
    }

    public GoogleConnector Google { get; }

    public IReadOnlyCollection<StdioMcpClient> Servers => _servers.Values;

    /// <summary>Запускает серверы из mcp.json (уже запущенные не трогает). Возвращает строки статуса.</summary>
    public async Task<List<string>> StartAsync(CancellationToken ct, bool restart = false)
    {
        if (restart)
            await StopAllAsync();

        var config = McpConfig.Load();
        var wanted = config.Servers
            .Where(p => !p.Value.Disabled && !string.IsNullOrWhiteSpace(p.Value.Command))
            .ToDictionary(p => McpConfig.Slug(p.Key), p => p.Value);

        foreach (var gone in _servers.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            await _servers[gone].DisposeAsync();
            _servers.Remove(gone);
        }

        var started = new List<StdioMcpClient>();
        foreach (var (slug, entry) in wanted)
        {
            if (slug == GoogleConnector.ServerSlug || _servers.ContainsKey(slug))
                continue;
            var client = new StdioMcpClient(slug, entry);
            _servers[slug] = client;
            started.Add(client);
        }
        await Task.WhenAll(started.Select(c => c.StartAsync(ct)));

        return started.Select(c => c.Error is null
            ? $"MCP «{c.Name}»: {c.Tools.Count} инструм."
            : $"MCP «{c.Name}» не запустился: {c.Error}").ToList();
    }

    /// <summary>Схемы для сервера (имена local__server__tool).</summary>
    public List<LocalToolSpec> Specs()
    {
        var specs = new List<LocalToolSpec>(Google.Tools);
        foreach (var server in _servers.Values.Where(s => s.Error is null))
        {
            foreach (var (tool, description, schema) in server.Tools)
            {
                var name = $"local__{server.Name}__{Sanitize(tool)}";
                if (name.Length <= 64)
                    specs.Add(new LocalToolSpec(name, $"[MCP {server.Name}] {description}", schema));
            }
        }
        return specs.Take(48).ToList();
    }

    public async Task<ToolResult> CallAsync(string name, string argumentsJson, PathGuard? project, CancellationToken ct)
    {
        var parts = name.Split("__", 3);
        if (parts.Length != 3)
            return ToolResult.Fail($"Неверное имя инструмента: {name}");
        var (server, tool) = (parts[1], parts[2]);

        if (server == GoogleConnector.ServerSlug)
            return await Google.CallAsync(tool, ToolArgs.Parse(argumentsJson), project, ct);

        if (!_servers.TryGetValue(server, out var client) || client.Error is not null)
            return ToolResult.Fail($"MCP-сервер «{server}» не запущен на ПК пользователя (/mcp — статус).");
        var original = client.Tools.FirstOrDefault(t => Sanitize(t.Tool) == tool).Tool ?? tool;

        // Сторонний MCP-сервер может писать файлы, выполнять команды и ходить в сеть — как в Claude Desktop,
        // спрашиваем (в авто-режиме подтверждение пропускается).
        var preview = argumentsJson.Length > 400 ? argumentsJson[..400] + "…" : argumentsJson;
        if (!await _approver.ApproveAsync($"MCP «{server}»", $"{original} {preview}".TrimEnd(), "ПК", ct))
            return ToolResult.Fail("Пользователь отклонил вызов инструмента. Не повторяй его; спроси, что делать.");

        try
        {
            var (text, isError) = await client.CallAsync(original, argumentsJson, ct);
            if (text.Length > MaxOutputChars)
                text = text[..MaxOutputChars] + $"\n[… ответ обрезан: всего {text.Length} символов]";
            return new ToolResult(text.Length == 0 ? "(пустой ответ)" : text, isError);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail($"MCP «{server}»: {ex.Message}");
        }
    }

    private static string Sanitize(string tool) =>
        new(tool.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(40).ToArray());

    private async Task StopAllAsync()
    {
        foreach (var s in _servers.Values)
            await s.DisposeAsync();
        _servers.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();
}

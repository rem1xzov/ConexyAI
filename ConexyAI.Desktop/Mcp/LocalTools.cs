using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop.Mcp;

/// <summary>
/// Все инструменты, которые десктоп предлагает агенту сверх файлов и терминала: встроенные Google, почта и сервисы
/// РФ, а также локальные MCP-серверы из mcp.json. Живёт весь сеанс (процессы MCP не перезапускаются на каждый ход).
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
        Mail = new MailConnector(approver);
        Services = new ServiceConnector(approver);
    }

    /// <summary>Лимит сервера на инструменты десктопа (DeviceToolSanitizer.MaxTools).</summary>
    public const int MaxTools = 48;

    public GoogleConnector Google { get; }

    public MailConnector Mail { get; }

    /// <summary>REST-сервисы РФ (Яндекс Диск, Telegram-бот, ВКонтакте, Битрикс24 …).</summary>
    public ServiceConnector Services { get; }

    /// <summary>Встроенные инструменты, которые всегда идут отдельными схемами.</summary>
    private int BuiltinCount => Google.Tools.Count + Mail.Tools.Count + Services.Tools.Count;

    private int McpToolCount => _servers.Values.Where(s => s.Error is null).Sum(s => s.Tools.Count);

    /// <summary>Сколько инструментов хотят показать агенту все подключённые сервисы (до обрезки по лимиту).</summary>
    public int WantedToolCount => BuiltinCount + McpToolCount;

    /// <summary>
    /// MCP-инструментов больше, чем помещается рядом со встроенными: вместо списка агент получает «найти» и «вызвать».
    /// </summary>
    public bool GatewayMode => McpToolCount > 0 && BuiltinCount + McpToolCount > MaxTools;

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
            if (IsReserved(slug) || _servers.ContainsKey(slug))
                continue;
            var client = new StdioMcpClient(slug, entry);
            _servers[slug] = client;
            started.Add(client);
        }
        await Task.WhenAll(started.Select(c => c.StartAsync(ct)));

        var lines = started.Select(c => c.Error is null
            ? $"MCP «{c.Name}»: {c.Tools.Count} инструм."
            : $"MCP «{c.Name}» не запустился: {c.Error}").ToList();
        if (GatewayMode)
            lines.Add($"Инструментов {WantedToolCount} — больше лимита {MaxTools}: агент работает с MCP через поиск (local__{McpGateway.ServerSlug}__find) и видит все.");
        return lines;
    }

    private static bool IsReserved(string slug) =>
        slug is GoogleConnector.ServerSlug or MailConnector.ServerSlug or ServiceConnector.ServerSlug or McpGateway.ServerSlug;

    /// <summary>Предупреждение, если часть инструментов не влезет в лимит. В режиме шлюза — не бывает: видны все.</summary>
    public string? LimitWarning() =>
        !GatewayMode && WantedToolCount > MaxTools
            ? $"Инструментов {WantedToolCount}, агенту доступно не больше {MaxTools} — лишние не попадут. Отключите ненужные: /mcp remove <имя>"
            : null;

    private List<GatewayTool> GatewayTools() =>
        _servers.Values.Where(s => s.Error is null)
            .SelectMany(s => s.Tools.Select(t => new GatewayTool(s.Name, t.Tool, t.Description, t.Schema)))
            .ToList();

    /// <summary>Схемы для сервера (имена local__server__tool).</summary>
    public List<LocalToolSpec> Specs()
    {
        var specs = new List<LocalToolSpec>(Google.Tools);
        specs.AddRange(Mail.Tools);
        specs.AddRange(Services.Tools);

        if (GatewayMode)
        {
            specs.AddRange(McpGateway.Specs(GatewayTools()));
            return specs.Take(MaxTools).ToList();
        }

        foreach (var server in _servers.Values.Where(s => s.Error is null))
        {
            foreach (var (tool, description, schema) in server.Tools)
            {
                var name = $"local__{server.Name}__{Sanitize(tool)}";
                if (name.Length <= 64)
                    specs.Add(new LocalToolSpec(name, $"[MCP {server.Name}] {description}", schema));
            }
        }
        return specs.Take(MaxTools).ToList();
    }

    public async Task<ToolResult> CallAsync(string name, string argumentsJson, PathGuard? project, CancellationToken ct)
    {
        var parts = name.Split("__", 3);
        if (parts.Length != 3)
            return ToolResult.Fail($"Неверное имя инструмента: {name}");
        var (server, tool) = (parts[1], parts[2]);

        if (server == GoogleConnector.ServerSlug)
            return await Google.CallAsync(tool, ToolArgs.Parse(argumentsJson), project, ct);
        if (server == MailConnector.ServerSlug)
            return await Mail.CallAsync(tool, ToolArgs.Parse(argumentsJson), project, ct);
        if (server == ServiceConnector.ServerSlug)
            return await Services.CallAsync(tool, ToolArgs.Parse(argumentsJson), ct);
        if (server == McpGateway.ServerSlug)
            return await CallGatewayAsync(tool, ToolArgs.Parse(argumentsJson), ct);

        if (!_servers.TryGetValue(server, out var client) || client.Error is not null)
            return ToolResult.Fail($"MCP-сервер «{server}» не запущен на ПК пользователя (/mcp — статус).");
        var original = client.Tools.FirstOrDefault(t => Sanitize(t.Tool) == tool).Tool ?? tool;
        return await CallServerToolAsync(client, original, argumentsJson, ct);
    }

    private async Task<ToolResult> CallGatewayAsync(string tool, ToolArgs args, CancellationToken ct)
    {
        if (tool == "find")
            return ToolResult.Ok(McpGateway.Find(GatewayTools(), args.Str("query"), args.Str("server"), args.Int("limit")));
        if (tool != "call")
            return ToolResult.Fail($"Неизвестный инструмент шлюза: {tool}");

        var serverName = args.Required("server", "mcp call");
        var toolName = args.Required("tool", "mcp call");
        var slug = McpConfig.Slug(serverName);
        if (!_servers.TryGetValue(slug, out var client) || client.Error is not null)
            return ToolResult.Fail($"MCP-сервер «{serverName}» не запущен (есть: {string.Join(", ", _servers.Values.Where(s => s.Error is null).Select(s => s.Name))}).");
        var original = client.Tools.FirstOrDefault(t => t.Tool.Equals(toolName, StringComparison.Ordinal)).Tool
                       ?? client.Tools.FirstOrDefault(t => t.Tool.Equals(toolName, StringComparison.OrdinalIgnoreCase)).Tool;
        if (original is null)
            return ToolResult.Fail($"У сервера «{client.Name}» нет инструмента «{toolName}». Найдите нужный через local__{McpGateway.ServerSlug}__find.");

        var arguments = args.Raw("arguments") is { ValueKind: System.Text.Json.JsonValueKind.Object } a ? a.GetRawText() : "{}";
        return await CallServerToolAsync(client, original, arguments, ct);
    }

    private async Task<ToolResult> CallServerToolAsync(StdioMcpClient client, string original, string argumentsJson, CancellationToken ct)
    {
        // Сторонний MCP-сервер может писать файлы, выполнять команды и ходить в сеть — как в Claude Desktop,
        // спрашиваем (в авто-режиме подтверждение пропускается).
        var preview = argumentsJson.Length > 400 ? argumentsJson[..400] + "…" : argumentsJson;
        if (!await _approver.ApproveAsync($"MCP «{client.Name}»", $"{original} {preview}".TrimEnd(), "ПК", ct))
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
            return ToolResult.Fail($"MCP «{client.Name}»: {ex.Message}");
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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConexyAI.Desktop.Mcp;

/// <summary>Инструмент, который десктоп предлагает агенту (имя уже в виде local__server__tool).</summary>
public sealed record LocalToolSpec(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Клиент локального MCP-сервера по stdio (JSON-RPC 2.0, одно сообщение — одна строка), как в Claude Desktop:
/// процесс запускается командой из mcp.json и живёт, пока открыт чат.
/// </summary>
public sealed class StdioMcpClient : IAsyncDisposable
{
    private readonly string _name;
    private readonly McpServerEntry _entry;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private long _nextId;

    public StdioMcpClient(string name, McpServerEntry entry)
    {
        _name = name;
        _entry = entry;
    }

    public string Name => _name;
    public IReadOnlyList<(string Tool, string Description, JsonElement Schema)> Tools { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var psi = BuildStartInfo();
            _process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не запустился");
            _ = Task.Run(ReadLoopAsync);
            _ = Task.Run(async () =>
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                    lock (_stderr)
                        if (_stderr.Length < 4000) _stderr.AppendLine(line);
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // npx при первом запуске качает пакет; mcp-remote ждёт входа в браузере — у таких серверов срок дольше.
            var startup = _entry.StartupSeconds > 0 ? _entry.StartupSeconds : 60;
            timeout.CancelAfter(TimeSpan.FromSeconds(startup));
            await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "conexy", ["version"] = Ui.Version },
            }, timeout.Token);
            await NotifyAsync("notifications/initialized");

            var list = await RequestAsync("tools/list", new JsonObject(), timeout.Token);
            var tools = new List<(string, string, JsonElement)>();
            if (list?["tools"] is JsonArray array)
            {
                foreach (var t in array)
                {
                    var toolName = t?["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(toolName))
                        continue;
                    var schema = t!["inputSchema"] is JsonObject s
                        ? JsonDocument.Parse(s.ToJsonString()).RootElement.Clone()
                        : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
                    tools.Add((toolName, t["description"]?.GetValue<string>() ?? string.Empty, schema));
                }
            }
            Tools = tools;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            string err;
            lock (_stderr) err = _stderr.ToString().Trim();
            var waited = _entry.StartupSeconds > 0 ? _entry.StartupSeconds : 60;
            Error = (ex is OperationCanceledException ? $"не ответил за {waited} с" : ex.Message)
                    + (err.Length > 0 ? " · " + err.Split('\n').Last().Trim() : string.Empty);
            await DisposeAsync();
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var command = _entry.Command;
        var args = (_entry.Args ?? []).Select(_entry.Expand).ToList();
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows() && command.Equals("npx", StringComparison.OrdinalIgnoreCase) && FindNpxCli() is { } npx)
        {
            // npx — это npx.cmd, а cmd.exe заново разбирает строку: `&`, `^`, `%` в строке подключения к БД
            // или токене ломали бы запуск. Запускаем node с npx-cli.js напрямую — аргументы идут как есть.
            psi = new ProcessStartInfo(npx.Node);
            psi.ArgumentList.Add(npx.Cli);
        }
        else if (OperatingSystem.IsWindows() && !Path.HasExtension(command) && !File.Exists(command))
        {
            // uvx/прочие .cmd — через cmd.exe; каждый аргумент в кавычках, чтобы `&` и `|` не стали командами.
            psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = "/d /s /c \"" + string.Join(' ', new[] { command }.Concat(args).Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")) + "\"",
            };
            args = [];
        }
        else
        {
            psi = new ProcessStartInfo(command);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        foreach (var (k, v) in _entry.Env ?? [])
            psi.Environment[k] = _entry.Expand(v);
        return psi;
    }

    /// <summary>node.exe и npx-cli.js из установки Node.js в PATH.</summary>
    internal static (string Node, string Cli)? FindNpxCli()
    {
        var path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var node = Path.Combine(dir, "node.exe");
                var cli = Path.Combine(dir, "node_modules", "npm", "bin", "npx-cli.js");
                if (File.Exists(node) && File.Exists(cli))
                    return (node, cli);
            }
            catch (ArgumentException) { /* мусор в PATH */ }
        }
        return null;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_process is { HasExited: false } p && await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                    continue; // некоторые серверы пишут логи в stdout
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (msg?["id"] is JsonValue idNode && idNode.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var tcs))
                {
                    if (msg["error"] is { } error)
                        tcs.TrySetException(new InvalidOperationException(error["message"]?.GetValue<string>() ?? error.ToJsonString()));
                    else
                        tcs.TrySetResult(msg["result"]);
                }
            }
        }
        catch (Exception) { /* процесс завершился */ }
        foreach (var (_, tcs) in _pending)
            tcs.TrySetException(new InvalidOperationException($"MCP-сервер {_name} завершился."));
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        await using var reg = ct.Register(() => { _pending.TryRemove(id, out _); tcs.TrySetCanceled(ct); });
        return await tcs.Task;
    }

    private Task NotifyAsync(string method) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    private async Task WriteAsync(JsonObject message)
    {
        if (_process is null)
            throw new InvalidOperationException($"MCP-сервер {_name} не запущен.");
        await _writeLock.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString());
            await _process.StandardInput.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Вызов инструмента; текстовые части результата склеиваются в одну строку.</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string tool, string argumentsJson, CancellationToken ct)
    {
        var args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? new JsonObject();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, timeout.Token);

        var sb = new StringBuilder();
        if (result?["content"] is JsonArray parts)
        {
            foreach (var part in parts)
            {
                switch (part?["type"]?.GetValue<string>())
                {
                    case "text":
                        sb.AppendLine(part["text"]?.GetValue<string>());
                        break;
                    case "resource":
                        sb.AppendLine(part["resource"]?["text"]?.GetValue<string>() ?? part["resource"]?["uri"]?.GetValue<string>());
                        break;
                    case { } other:
                        sb.AppendLine($"[{other}]");
                        break;
                }
            }
        }
        else if (result is not null)
        {
            sb.Append(result.ToJsonString());
        }
        return (sb.ToString().Trim(), result?["isError"]?.GetValue<bool>() == true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
            return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* уже завершился */ }
        _process.Dispose();
        _process = null;
        await Task.CompletedTask;
    }
}

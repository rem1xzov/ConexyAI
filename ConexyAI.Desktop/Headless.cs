using System.Runtime.InteropServices;
using System.Text.Json;
using ConexyAI.Desktop.Mcp;
using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>
/// HEADLESS: неинтерактивный запуск для CI и скриптов.
/// <code>
/// conexy -p "исправь тесты" --output-format json
/// git diff main | conexy -p "review these changes" --permission-mode default
/// </code>
/// Команды по умолчанию не подтверждаются (безопасно для CI); <c>--auto</c> разрешает их,
/// но отправка писем и другие чувствительные действия всё равно отклоняются.
/// </summary>
public static class Headless
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object OutSync = new();

    public static async Task<int> RunAsync(string[] args)
    {
        var prompt = Prompt(args);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("Нет запроса. Используйте: conexy -p \"что сделать\" (или передайте текст в stdin).");
            return 64;
        }

        var dir = Opt(args, "--dir") ?? Directory.GetCurrentDirectory();
        string root;
        try
        {
            root = Path.GetFullPath(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine("Неверная папка: " + ex.Message);
            return 64;
        }
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine("Папка не найдена: " + root);
            return 66;
        }
        if (Workspace.WhyUnsafe(root) is { } unsafeRoot)
        {
            Console.Error.WriteLine($"Рабочая папка «{root}» не подходит: {unsafeRoot}.");
            return 65;
        }

        if (Opt(args, "--proxy") is { Length: > 0 } proxy)
        {
            if (!NetProxy.IsValid(proxy))
            {
                Console.Error.WriteLine("Неверный прокси: нужен http://, https://, socks4:// или socks5://.");
                return 64;
            }
            Environment.SetEnvironmentVariable("CONEXY_HTTP_PROXY", proxy);
        }
        if (args.Contains("--no-hooks"))
            Environment.SetEnvironmentVariable("CONEXY_DISABLE_HOOKS", "1");

        var output = OutputFormat(args);
        var config = ClientConfig.Load();
        var serverUrl = Opt(args, "--url") ?? config.ServerUrl;
        var key = Opt(args, "--key") ?? config.GetKey();
        if (!ClientConfig.IsAllowedServerUrl(serverUrl) || string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("Нет входа: нужен https-адрес сервера и ключ. Выполните: conexy login");
            return 2;
        }

        var chatId = Opt(args, "--chat") is { Length: > 0 } chat && Guid.TryParse(chat, out var parsed) ? parsed : Guid.NewGuid();
        var mode = (Opt(args, "--agent") ?? Opt(args, "--model"))?.ToLowerInvariant() switch
        {
            "cowork" or "conexy-cowork" => "cowork",
            _ => "coder",
        };
        var permission = (Opt(args, "--permission-mode") ?? string.Empty).ToLowerInvariant();
        var auto = args.Contains("--auto") || args.Contains("--dangerously-skip-permissions")
                   || permission is "auto" or "bypasspermissions";
        var plan = args.Contains("--plan") || permission is "plan";
        var effort = Opt(args, "--effort") ?? Environment.GetEnvironmentVariable("CONEXY_EFFORT") ?? "high";
        var maxTurns = int.TryParse(Opt(args, "--max-turns"), out var n) && n > 0 ? n : 0;

        var approver = new HeadlessApprover(auto);
        Action<string> log = args.Contains("--verbose")
            ? line => Console.Error.WriteLine("[conexy] " + SecretMask.Mask(line))
            : _ => { };

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lifetime.Cancel();
        };
        using var sigterm = RegisterSigterm(lifetime);
        if (int.TryParse(Opt(args, "--timeout"), out var timeoutSeconds) && timeoutSeconds > 0)
            lifetime.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        LocalTools? local = null;
        ServerClient? server = null;
        AgentSession? session = null;
        try
        {
            server = new ServerClient(serverUrl!.TrimEnd('/'), key.Trim());
            if (output == "stream-json")
            {
                WriteStream(output, new
                {
                    type = "system",
                    subtype = "init",
                    session_id = chatId.ToString(),
                    model = mode,
                    cwd = root,
                });
            }

            // Ранняя проверка ключа: 401/429 дают понятный код выхода, а не общую ошибку.
            await server.GetTokenAsync(lifetime.Token);

            if (args.Contains("--with-mcp"))
            {
                local = new LocalTools(approver);
                foreach (var line in await local.StartAsync(lifetime.Token))
                    log(line);
            }

            var device = new DeviceTools(daemon: false, approver, log, server.GetGitHubTokenAsync, local, root);
            var tools = new ToolRunner(root, device.Approver, log, server.GetGitHubTokenAsync, local);
            session = new AgentSession(server, tools, chatId, device)
            {
                Local = local,
                Headless = true,
                Mode = mode,
                Effort = effort,
                MaxToolCalls = maxTurns,
                Orchestra = args.Contains("--team") || args.Contains("--orchestra"),
            };
            if (plan)
                session.PlanMode = true;
            session.Event = evt => Emit(output, evt);

            await session.ConnectAsync(lifetime.Token);
            var error = await session.AskAsync(prompt.Trim(), lifetime.Token);
            var result = session.LastAnswer;
            return Finish(output, result, error, chatId, session.LastTaskId);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Остановлено.");
            return 130;
        }
        catch (AuthRejectedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Лимит тарифа", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ещё идёт", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine("Сервер недоступен: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
            if (local is not null)
                await local.DisposeAsync();
            server?.Dispose();
        }
    }

    /// <summary>Текст запроса: --prompt, позиционный аргумент или stdin (для пайпов).</summary>
    private static string? Prompt(string[] args)
    {
        if (Opt(args, "--prompt") is { Length: > 0 } explicitPrompt)
            return explicitPrompt;

        var takesValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--dir", "--chat", "--agent", "--model", "--effort", "--max-turns", "--output-format",
            "--permission-mode", "--url", "--key", "--proxy", "--timeout", "--prompt",
        };
        string? positional = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-p" or "--print")
                continue;
            if (takesValue.Contains(arg))
            {
                i++;
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal) || arg is "-h" or "--help")
                continue;
            positional ??= arg;
        }

        // Пайп: `git diff | conexy -p "review"` — к запросу добавляем содержимое stdin.
        var fromStdin = Console.IsInputRedirected ? Console.In.ReadToEnd().Trim() : string.Empty;
        if (positional is not null)
            return fromStdin.Length == 0 ? positional : positional + "\n\n---\n" + fromStdin;
        return fromStdin.Length > 0 ? fromStdin : null;
    }

    private static string OutputFormat(string[] args)
    {
        var format = Opt(args, "--output-format")?.ToLowerInvariant();
        if (format is "json" or "stream-json" or "text")
            return format;
        return args.Contains("--json") ? "json" : "text";
    }

    private static int Finish(string output, string result, string? error, Guid chatId, Guid? taskId)
    {
        if (output == "text")
        {
            if (!string.IsNullOrEmpty(result))
                Console.Out.Write(result.EndsWith('\n') ? result : result + "\n");
            if (error is not null)
                Console.Error.WriteLine(error);
            return string.IsNullOrEmpty(error) ? 0 : 1;
        }

        WriteStream(output, new
        {
            type = "result",
            subtype = error is null ? "success" : "error",
            result,
            error,
            session_id = chatId.ToString(),
            task_id = taskId?.ToString(),
            is_error = error is not null,
        });
        return string.IsNullOrEmpty(error) ? 0 : 1;
    }

    private static void Emit(string output, AgentEvent evt)
    {
        if (output != "stream-json")
            return;
        switch (evt)
        {
            case AgentContent content:
                WriteStream(output, new { type = "assistant_delta", text = content.Delta });
                break;
            case AgentTool tool:
                WriteStream(output, new
                {
                    type = "tool",
                    name = tool.Name,
                    stage = tool.Stage,
                    detail = tool.Detail,
                    is_error = tool.IsError,
                });
                break;
            case AgentStatus status:
                WriteStream(output, new { type = "status", stage = status.Stage, label = status.Label });
                break;
        }
    }

    private static void WriteStream(string format, object payload)
    {
        if (format != "stream-json" && format != "json")
            return;
        lock (OutSync)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, Json));
            Console.Out.Flush();
        }
    }

    private static string? Opt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static IDisposable? RegisterSigterm(CancellationTokenSource cts)
    {
        if (OperatingSystem.IsWindows())
            return null;
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cts.Cancel();
            });
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// HEADLESS: подтверждения из CI. По умолчанию команды запрещены; --auto разрешает их.
/// Письма и другие действия от имени пользователя запрещены всегда: спросить некого.
/// </summary>
public sealed class HeadlessApprover(bool auto) : ICommandApprover
{
    public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(auto);

    public Task<bool> ApproveAsync(string title, string details, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(auto);

    public Task<bool> ApproveCommandAsync(string command, string? description, string workingDirectory, CancellationToken ct) =>
        Task.FromResult(auto);

    public Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

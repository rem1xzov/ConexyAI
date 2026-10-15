using System.Security.Cryptography;
using System.Text;
using ConexyAI.Desktop;
using ConexyAI.Desktop.Tools;

const string Usage = """
    ConexyAI Desktop — агент правит код на этом ПК, мозг работает на сервере.

      conexy login [--url <адрес>] [--key <ключ>]   сохранить ключ (выпускается на сайте в настройках)
      conexy [--dir <папка>]                        чат с агентом; рабочая папка — текущая или --dir
      conexy logout                                 забыть ключ
      conexy selftest                               проверить локальные инструменты без сервера

    Агент видит и меняет файлы ТОЛЬКО внутри рабочей папки. Любая команда терминала,
    кроме чтения, выполняется после вашего подтверждения.
    """;

const string DefaultServerUrl = "https://conexyai.ru";

static string? NormalizeUrl(string? url)
{
    url = url?.Trim().Trim('"');
    if (string.IsNullOrEmpty(url))
        return null;
    if (!url.Contains("://"))
        url = "https://" + url;
    return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? url.TrimEnd('/')
        : null;
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var exitCode = await RunAsync(args);
// Запуск двойным кликом: не закрываем окно, пока пользователь не прочитает сообщение.
if (exitCode != 0 && args.Length == 0 && !Console.IsInputRedirected)
{
    Console.WriteLine("\nНажмите Enter, чтобы закрыть окно...");
    Console.ReadLine();
}
return exitCode;

static async Task<int> RunAsync(string[] args)
{
var command = args.FirstOrDefault(a => !a.StartsWith("--"));
try
{
    switch (command)
    {
        case "-h" or "--help" or "help":
            Console.WriteLine(Usage);
            return 0;

        case "selftest":
            return await SelfTest.RunAsync();

        case "logout":
            ClientConfig.Delete();
            Console.WriteLine("Ключ удалён.");
            return 0;

        case "login":
            return await LoginAsync(args);

        default:
            if (args.Any(a => a is "-h" or "--help"))
            {
                Console.WriteLine(Usage);
                return 0;
            }
            return await ChatAsync(args);
    }
}
catch (AuthRejectedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine("Не удалось связаться с сервером: " + ex.Message);
    return 3;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine("Ошибка: " + ex.Message);
    return 4;
}
}

static async Task<int> LoginAsync(string[] args)
{
    var config = ClientConfig.Load();
    var url = Option(args, "--url") ?? config.ServerUrl;
    var key = Option(args, "--key");
    if (string.IsNullOrWhiteSpace(url))
    {
        Console.Write($"Адрес сервера [Enter = {DefaultServerUrl}]: ");
        var line = Console.ReadLine()?.Trim();
        // Пользователь мог вставить целую команду «conexy login --url ... --key ...» — разбираем её.
        if (!string.IsNullOrEmpty(line) && line.Contains("--"))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            url = Option(tokens, "--url");
            key ??= Option(tokens, "--key");
        }
        else
        {
            url = string.IsNullOrEmpty(line) ? DefaultServerUrl : line;
        }
    }

    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Write("Ключ (cnx_...): ");
        key = ReadSecret();
    }

    url = NormalizeUrl(url);
    if (url is null)
    {
        Console.Error.WriteLine("Неверный адрес сервера. Пример: https://conexyai.ru");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("Нужны адрес сервера и ключ.");
        return 1;
    }

    // Проверяем сразу, чтобы не сохранить заведомо негодный ключ.
    using (var server = new ServerClient(url, key.Trim()))
        await server.GetTokenAsync();

    config.ServerUrl = url.TrimEnd('/');
    config.SetKey(key.Trim());
    config.Save();
    Console.WriteLine("Готово: ключ принят и сохранён. Запускайте: conexy");
    return 0;
}

static async Task<int> ChatAsync(string[] args)
{
    var config = ClientConfig.Load();
    var key = config.GetKey();
    if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(key))
    {
        // Первый запуск (например двойным кликом): спрашиваем данные прямо здесь.
        Console.WriteLine("Вы ещё не вошли. Ключ выпускается на сайте в настройках.\n");
        var loginCode = await LoginAsync([]);
        if (loginCode != 0)
            return loginCode;
        config = ClientConfig.Load();
        key = config.GetKey();
    }

    var root = Path.GetFullPath(Option(args, "--dir") ?? Directory.GetCurrentDirectory());
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"Папка не найдена: {root}");
        return 1;
    }

    using var server = new ServerClient(config.ServerUrl!, key!);
    await server.GetTokenAsync(); // ранняя проверка ключа

    var approver = new ConsoleApprover();
    var firstBanner = true;
    while (true)
    {
        var next = await ChatInFolderAsync(server, approver, root, firstBanner);
        if (next is null)
            return 0;
        root = next;
        firstBanner = false;
    }
}

static void PrintBanner(string root)
{
    var lines = new[]
    {
        "  ConexyAI — AI-агент для вашего кода",
        $"  Папка: {root}",
        "  /help — команды, /cd — сменить папку, /ls — содержимое",
    };
    var width = lines.Max(l => l.Length) + 2;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╭" + new string('─', width) + "╮");
    foreach (var l in lines)
        Console.WriteLine("│" + l.PadRight(width) + "│");
    Console.WriteLine("╰" + new string('─', width) + "╯");
    Console.ResetColor();
}

static void PrintHelp() => Console.WriteLine("""
    Команды:
      /ls [путь]      показать содержимое папки (папки пронумерованы)
      /cd <путь|N|..> сменить рабочую папку (N — номер из /ls)
      /pwd            текущая папка
      /help           эта справка
      /exit           выход (также: пустая строка или exit)
    Всё остальное — задача для агента. Ctrl+C во время ответа — остановить.
    Токен GitHub и MCP-серверы берутся из настроек на сайте автоматически.
    """);

// Возвращает новую папку, если пользователь её сменил, или null для выхода.
static async Task<string?> ChatInFolderAsync(ServerClient server, ConsoleApprover approver, string root, bool firstBanner)
{
    var tools = new ToolRunner(root, approver, line => Console.WriteLine($"\n  ▸ {line}"), server.GetGitHubTokenAsync);
    await using var session = new AgentSession(server, tools, ChatIdFor(root));
    await session.ConnectAsync(CancellationToken.None);

    PrintBanner(root);
    if (firstBanner)
        Console.WriteLine("Пишите задачу. Ctrl+C во время ответа — остановить.\n");
    var listed = new List<string>();

    while (true)
    {
        Console.Write("> ");
        var prompt = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            return null;

        if (prompt.StartsWith('/'))
        {
            var parts = prompt.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
            var arg = parts.Length > 1 ? parts[1].Trim('"') : string.Empty;
            switch (parts[0].ToLowerInvariant())
            {
                case "/exit" or "/quit":
                    return null;
                case "/help":
                    PrintHelp();
                    break;
                case "/pwd":
                    Console.WriteLine(root);
                    break;
                case "/ls":
                    ListFolder(arg.Length == 0 ? root : ResolveFolder(root, arg, listed), listed);
                    break;
                case "/cd":
                    var target = ResolveFolder(root, arg, listed);
                    if (Directory.Exists(target))
                        return Path.GetFullPath(target);
                    Console.Error.WriteLine($"Папка не найдена: {target}");
                    break;
                default:
                    Console.Error.WriteLine("Неизвестная команда. /help — список.");
                    break;
            }
            continue;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var error = await session.AskAsync(prompt, cts.Token);
            if (error is not null)
                Console.Error.WriteLine("\n[!] " + error);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("[!] " + ex.Message);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}

static string ResolveFolder(string root, string arg, List<string> listed)
{
    if (arg.Length == 0)
        return root;
    if (int.TryParse(arg, out var n) && n >= 1 && n <= listed.Count)
        return listed[n - 1];
    if (arg == "~")
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.GetFullPath(Path.Combine(root, arg));
}

static void ListFolder(string dir, List<string> listed)
{
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"Папка не найдена: {dir}");
        return;
    }

    listed.Clear();
    Console.WriteLine(dir);
    try
    {
        var parent = Directory.GetParent(dir);
        if (parent is not null)
        {
            listed.Add(parent.FullName);
            Console.WriteLine($"  {listed.Count,3}. ..");
        }
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj")
                continue;
            listed.Add(d);
            Console.WriteLine($"  {listed.Count,3}. {name}/");
        }
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"       {Path.GetFileName(f)}");
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Нет доступа к папке.");
    }
}

// Один чат на папку: тот же id при каждом запуске, поэтому история диалога продолжается.
static Guid ChatIdFor(string root)
{
    var normalized = OperatingSystem.IsWindows() ? root.ToLowerInvariant() : root;
    return new Guid(MD5.HashData(Encoding.UTF8.GetBytes("conexy-desktop:" + normalized)));
}

static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) sb.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
        }
    }
}

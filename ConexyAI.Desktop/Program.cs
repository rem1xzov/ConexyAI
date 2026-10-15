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

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

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

static async Task<int> LoginAsync(string[] args)
{
    var config = ClientConfig.Load();
    var url = Option(args, "--url") ?? config.ServerUrl;
    if (string.IsNullOrWhiteSpace(url))
    {
        Console.Write("Адрес сервера (например https://conexy.example.com): ");
        url = Console.ReadLine()?.Trim();
    }

    var key = Option(args, "--key");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Write("Ключ (cnx_...): ");
        key = ReadSecret();
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
        Console.Error.WriteLine("Вы не вошли. Выполните: conexy login");
        return 1;
    }

    var root = Path.GetFullPath(Option(args, "--dir") ?? Directory.GetCurrentDirectory());
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"Папка не найдена: {root}");
        return 1;
    }

    using var server = new ServerClient(config.ServerUrl, key);
    await server.GetTokenAsync(); // ранняя проверка ключа

    var tools = new ToolRunner(root, new ConsoleApprover(), line => Console.WriteLine($"\n  ▸ {line}"));
    await using var session = new AgentSession(server, tools, ChatIdFor(root));
    await session.ConnectAsync(CancellationToken.None);

    Console.WriteLine($"ConexyAI Desktop. Рабочая папка: {root}");
    Console.WriteLine("Пишите задачу. Ctrl+C во время ответа — остановить, пустая строка или 'exit' — выйти.\n");

    while (true)
    {
        Console.Write("> ");
        var prompt = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            return 0;

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

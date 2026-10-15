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

Ui.Init();

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

        case "updatetest":
            return Updater.TestSchedule(args[1], args[2]) ?? 1;

        case "cliptest":
            var (clipText, clipMessage) = Attachments.PasteFromClipboard();
            Console.WriteLine($"text={clipText ?? "-"} message={clipMessage ?? "-"} attached={Attachments.Pending.Count}");
            return 0;

        case "mdtest":
            Ui.AssistantStart();
            Ui.Md.Feed("# Итог\n\n**1. Ассеты** не копируются в `wwwroot`. Это довольно длинный текст, который должен переноситься по ширине окна консоли, а не вылезать за рамки `pony.ini` и `app.js`.\n\n- пункт списка с `кодом` и **жирным** текстом и очень длинным продолжением для проверки переноса строки в списке\n2. нумерованный ✅ пункт\n\n```csharp\nvar x = 1;\n```\n\n> цитата\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
            Ui.FlushText();
            return 0;

        case "update":
            return await UpdateCommandAsync();

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

    // Ранняя проверка ключа. Если ключ отозван или заменён — предлагаем ввести новый, а не падаем.
    ServerClient? verified = null;
    for (var attempt = 0; verified is null; attempt++)
    {
        var candidate = new ServerClient(config.ServerUrl!, key!);
        try
        {
            await candidate.GetTokenAsync();
            verified = candidate;
        }
        catch (AuthRejectedException ex) when (ex.Message.Contains("недействителен") && attempt < 4 && !Console.IsInputRedirected)
        {
            candidate.Dispose();
            Ui.Error("Ключ недействителен или отозван.");
            if (Environment.GetEnvironmentVariable("CONEXY_KEY") is { Length: > 0 })
            {
                Console.Error.WriteLine("Ключ задан переменной окружения CONEXY_KEY — обновите её.");
                return 2;
            }

            Console.WriteLine("Введите новый ключ (выпускается на сайте в настройках).\n");
            try
            {
                if (await LoginAsync(["--url", config.ServerUrl!]) != 0)
                    return 1;
            }
            catch (AuthRejectedException loginEx)
            {
                Ui.Error(loginEx.Message);
            }

            config = ClientConfig.Load();
            key = config.GetKey() ?? string.Empty;
        }
    }
    using var server = verified;

    Updater.CleanupOld();
    if (!args.Contains(Updater.SkipFlag) && !Console.IsInputRedirected && await Updater.CheckAsync(server) is { } update)
    {
        Console.Write($"Доступна новая версия v{update.Version} (у вас v{Ui.Version}). Обновить сейчас? [Y/n] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is not ("n" or "н" or "no" or "нет"))
        {
            var code = await Updater.ApplyAsync(update, args);
            if (code is not null)
                return code.Value;
        }
    }

    var approver = new ConsoleApprover { AutoMode = args.Contains("--auto") };
    var store = ChatStore.Load();
    var chatId = store.ChatFor(root, ChatIdFor(root));
    var firstBanner = true;
    while (true)
    {
        var next = await ChatInFolderAsync(server, approver, store, root, chatId, firstBanner);
        if (next is null)
            return 0;
        (root, chatId) = next.Value;
        firstBanner = false;
    }
}

static string Ago(DateTime utc)
{
    var span = DateTime.UtcNow - utc;
    if (span.TotalMinutes < 1) return "только что";
    if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} мин назад";
    if (span.TotalDays < 1) return $"{(int)span.TotalHours} ч назад";
    return $"{(int)span.TotalDays} дн назад";
}

static async Task ShowHistoryAsync(ServerClient server, Guid chatId)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var messages = await server.GetTranscriptAsync(chatId, cts.Token);
        if (messages.Count == 0)
            return;
        var tail = messages.TakeLast(4).ToList();
        Ui.Info($"  История чата (последние {tail.Count} из {messages.Count}):");
        foreach (var m in tail)
            Ui.HistoryLine(m.Role, m.Content);
        Console.WriteLine();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthRejectedException)
    {
        // История — бонус: без неё работать можно.
    }
}

// /chats: список чатов с сервера (те же, что на сайте) + локальная привязка к папкам.
static async Task<(string Root, Guid ChatId)?> PickChatAsync(ServerClient server, ChatStore store, string root, Guid current)
{
    List<ChatItem> chats;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        chats = await server.GetChatsAsync(cts.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthRejectedException)
    {
        Ui.Error("Не удалось загрузить чаты: " + ex.Message);
        return null;
    }

    var items = chats
        .Where(c => c.Kind != "students" && (c.Model is null or "conexy-coder" || store.Folders.ContainsKey(c.Id)))
        .OrderByDescending(c => c.LastActivityAt)
        .Take(30)
        .ToList();

    Console.WriteLine();
    Console.WriteLine(Ui.Bold("Чаты") + Ui.Muted(" (синхронизированы с сайтом)"));
    Console.WriteLine($"  {0,3}. {Ui.Highlight("+ Новый чат")}");
    for (var i = 0; i < items.Count; i++)
    {
        var c = items[i];
        var title = c.Title ?? c.LastMessage ?? "Без названия";
        title = string.Join(' ', title.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > 42)
            title = title[..42] + "…";
        var folder = store.FolderOf(c.Id);
        var where = folder is null ? Ui.Muted("папка не выбрана") : Ui.Muted(folder);
        var mark = c.Id == current ? Ui.Green_("●") : " ";
        var pin = c.IsPinned ? "* " : string.Empty;
        Console.WriteLine($"  {i + 1,3}. {mark} {pin}{title}  {Ui.Muted($"{c.MessageCount} сообщ. · {Ago(c.LastActivityAt)}")}");
        Console.WriteLine($"         {where}");
    }

    Console.Write("\nНомер чата (Enter — отмена): ");
    var answer = Console.ReadLine()?.Trim();
    if (!int.TryParse(answer, out var n) || n < 0 || n > items.Count)
        return null;

    var chatId = n == 0 ? Guid.NewGuid() : items[n - 1].Id;
    var target = n == 0 ? null : store.FolderOf(chatId);
    if (target is null)
    {
        Console.Write($"Папка проекта для этого чата [Enter = {root}]: ");
        var line = Console.ReadLine()?.Trim().Trim('"');
        target = string.IsNullOrEmpty(line) ? root : Path.GetFullPath(Path.Combine(root, line));
        if (!Directory.Exists(target))
        {
            Ui.Error($"Папка не найдена: {target}");
            return null;
        }
    }

    store.Bind(chatId, target);
    return (target, chatId);
}


static async Task<int> UpdateCommandAsync()
{
    var config = ClientConfig.Load();
    var url = config.ServerUrl ?? DefaultServerUrl;
    using var server = new ServerClient(url, config.GetKey() ?? string.Empty);
    var info = await Updater.CheckAsync(server);
    if (info is null)
    {
        Console.WriteLine($"У вас последняя версия (v{Ui.Version}).");
        return 0;
    }

    var code = await Updater.ApplyAsync(info, [Updater.SkipFlag, "--updated"]);
    return code ?? 1;
}

static async Task<UsageInfo?> TryUsageAsync(ServerClient server)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await server.GetUsageAsync(cts.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthRejectedException)
    {
        return null;
    }
}

static void PrintHelp() => Console.WriteLine("""
    Команды:
      /ls [путь]      показать содержимое папки (папки пронумерованы)
      /cd <путь|N|..> сменить рабочую папку (N — номер из /ls)
      /pwd            текущая папка
      /attach <файл>  прикрепить файл/картинку (также: перетащите файл в окно или напишите @путь в сообщении)
      /paste          прикрепить картинку или файлы из буфера обмена (то же делает Ctrl+V)
      /detach [N]     убрать вложение по номеру или все
      /chats          список всех чатов (синхронизирован с сайтом): переключиться или создать новый,
                      у каждого чата своя папка проекта
      /team           вкл/выкл оркестр агентов: ведущий собирает команду субагентов (ProMax, Ultra, Admin)
      /usage          тариф и расход токенов агента
      /update         проверить обновления (или команда conexy update)
      /auto           вкл/выкл выполнение команд без подтверждения (или флаг --auto)
      /help           эта справка
      /exit           выход (также: exit)
    Всё остальное — задача для агента. Ctrl+C во время ответа — остановить.
    Токен GitHub и MCP-серверы берутся из настроек на сайте автоматически.
    """);

// Возвращает новую пару (папка, чат), если пользователь переключился, или null для выхода.
static async Task<(string Root, Guid ChatId)?> ChatInFolderAsync(
    ServerClient server, ConsoleApprover approver, ChatStore store, string root, Guid chatId, bool firstBanner)
{
    store.Bind(chatId, root);
    var tools = new ToolRunner(root, approver, Ui.ToolCall, server.GetGitHubTokenAsync);
    await using var session = new AgentSession(server, tools, chatId);
    try
    {
        await session.ConnectAsync(CancellationToken.None);
    }
    catch (Microsoft.AspNetCore.SignalR.HubException ex) when (ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
    {
        // Чат удалён на сайте или принадлежит другому аккаунту (ключ выпущен на другой аккаунт): забываем привязку, берём новый чат.
        store.Folders.Remove(chatId);
        store.Save();
        Ui.Info("Этот чат недоступен (удалён или чужой). Создаю новый.");
        return (root, Guid.NewGuid());
    }

    Ui.Banner(root, server.BaseUrl.Replace("https://", string.Empty));
    if (firstBanner && approver.AutoMode)
        Ui.Info("  Авто-режим включён: команды выполняются без подтверждения.");
    Ui.Info($"  Чат {chatId.ToString()[..8]} · /chats — список чатов, /cd — сменить проект");
    await ShowHistoryAsync(server, chatId);
    var listed = new List<string>();
    var usage = await TryUsageAsync(server);
    Ui.SetUsage(usage);
    Ui.TeamMode = session.Orchestra = Ui.TeamMode && usage?.TeamAllowed != false;

    while (true)
    {
        var prompt = Ui.ReadPrompt(approver.AutoMode);
        if (prompt is null || prompt.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(prompt) && Attachments.Pending.Count == 0)
            continue;

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
                case "/attach":
                    if (arg.Length == 0)
                        Ui.Info("Укажите файл: /attach путь  (или перетащите файл в окно, или Ctrl+V / /paste для буфера обмена)");
                    foreach (var file in arg.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var path = Path.IsPathRooted(file.Trim().Trim('"')) ? file : Path.Combine(root, file.Trim().Trim('"'));
                        if (Attachments.AddFile(path) is { } attachError)
                            Ui.Error(attachError);
                    }
                    if (Attachments.Pending.Count > 0)
                        Ui.Info("  Вложения: " + Attachments.Describe());
                    break;
                case "/paste":
                    var (pastedText, pasteMessage) = Attachments.PasteFromClipboard();
                    if (pastedText is not null)
                        Ui.Info("  В буфере текст — вставьте его в строку ввода (Ctrl+V).");
                    else if (pasteMessage is not null)
                        Ui.Info("  " + pasteMessage);
                    break;
                case "/detach":
                    if (arg.Length == 0)
                        Attachments.Clear();
                    else if (!int.TryParse(arg, out var number) || !Attachments.Remove(number))
                        Ui.Error("Нет вложения с таким номером.");
                    Ui.Info(Attachments.Pending.Count == 0 ? "  Вложений нет." : "  Вложения: " + Attachments.Describe());
                    break;
                case "/chats":
                    if (await PickChatAsync(server, store, root, chatId) is { } picked)
                        return picked;
                    break;
                case "/usage":
                    usage = await TryUsageAsync(server);
                    Ui.SetUsage(usage);
                    if (usage is null)
                        Ui.Error("Не удалось получить данные о тарифе.");
                    else
                        Ui.PrintStatus();
                    break;
                case "/team":
                    usage ??= await TryUsageAsync(server);
                    if (usage is { TeamAllowed: false })
                    {
                        Ui.Error($"Оркестр агентов доступен на ProMax и Ultra (ваш тариф: {usage.Tier}).");
                        break;
                    }
                    Ui.TeamMode = session.Orchestra = !session.Orchestra;
                    Ui.Info(session.Orchestra
                        ? "Оркестр включён: для больших задач агент соберёт команду субагентов."
                        : "Оркестр выключен: работает один агент.");
                    break;
                case "/update":
                    if (await Updater.CheckAsync(server) is { } found)
                    {
                        var code = await Updater.ApplyAsync(found, [Updater.SkipFlag]);
                        if (code is not null)
                            Environment.Exit(code.Value);
                    }
                    else
                        Ui.Info($"У вас последняя версия (v{Ui.Version}).");
                    break;
                case "/auto":
                    approver.AutoMode = !approver.AutoMode;
                    Ui.Info(approver.AutoMode
                        ? "Авто-режим включён: команды выполняются без подтверждения (опасные по-прежнему блокируются)."
                        : "Авто-режим выключен: команды требуют подтверждения.");
                    break;
                case "/ls":
                    ListFolder(arg.Length == 0 ? root : ResolveFolder(root, arg, listed), listed);
                    break;
                case "/cd":
                    var target = ResolveFolder(root, arg, listed);
                    if (Directory.Exists(target))
                    {
                        var full = Path.GetFullPath(target);
                        return (full, store.ChatFor(full, ChatIdFor(full)));
                    }
                    Console.Error.WriteLine($"Папка не найдена: {target}");
                    break;
                default:
                    Console.Error.WriteLine("Неизвестная команда. /help — список.");
                    break;
            }
            continue;
        }

        // Пути файлов в тексте (перетащили в окно / @файл) становятся вложениями.
        var attachedBefore = Attachments.Pending.Count;
        var attachErrors = new List<string>();
        prompt = Attachments.ExtractFiles(prompt, root, attachErrors);
        foreach (var message in attachErrors)
            Ui.Error(message);
        if (Attachments.Pending.Count > attachedBefore)
            Ui.Info("  + " + string.Join(", ", Attachments.Pending.Skip(attachedBefore).Select(a => a.FileName)));
        if (prompt.Length == 0)
        {
            if (Attachments.Pending.Count == 0)
                continue;
            prompt = "Посмотри вложения.";
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
                Ui.Error(error);
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(ex.Message);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
        usage = await TryUsageAsync(server) ?? usage;
        Ui.SetUsage(usage);
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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ConexyAI.Desktop;
using ConexyAI.Desktop.Mcp;
using ConexyAI.Desktop.Tools;

const string Usage = """
    ConexyAI Desktop — агент правит код на этом ПК, мозг работает на сервере.

      conexy login [--url <адрес>] [--key <ключ>]   сохранить ключ (выпускается на сайте в настройках)
      conexy [--dir <папка>]                        чат с агентом; рабочая папка — текущая или --dir
      conexy logout                                 забыть ключ
      conexy selftest                               проверить локальные инструменты без сервера

    Задачи с сайта на этом ПК:
      conexy projects                               список проектов, доступных сайту
      conexy projects add <папка> [--name <имя>]   добавить проект (имя по умолчанию — имя папки)
      conexy projects remove <id|имя>             убрать проект
      conexy daemon                                 фоновый режим: выполнять задачи с сайта, подтверждения — на сайте
      conexy daemon --install | --uninstall         включить/отключить автозапуск демона при входе в систему

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
            return await SelfTest.RunAsync(args);

        case "logout":
            ClientConfig.Delete();
            Console.WriteLine("Ключ удалён.");
            return 0;

        case "login":
            return await LoginAsync(args);

        // WEB_DEVICE: добавлено 2026-10-18
        case "daemon":
            return await Daemon.RunAsync(args);

        // WEB_DEVICE: добавлено 2026-10-18
        case "projects":
            return ProjectsCommand(args);

        case "updatetest":
            return Updater.TestSchedule(args[1], args[2]) ?? 1;

        case "helptest":
            PrintHelp();
            return 0;

        case "cliptest":
            var (clipText, clipMessage) = Attachments.PasteFromClipboard();
            Console.WriteLine($"text={clipText ?? "-"} message={clipMessage ?? "-"} attached={Attachments.Pending.Count}");
            return 0;

        case "mdtest":
            Ui.AssistantStart();
            Ui.Md.Feed("# Итог\n\n**1. Ассеты** не копируются в `wwwroot`. Это довольно длинный текст, который должен переноситься по ширине окна консоли, а не вылезать за рамки `pony.ini` и `app.js`.\n\n- пункт списка с `кодом` и **жирным** текстом и очень длинным продолжением для проверки переноса строки в списке\n2. нумерованный ✅ пункт\n\n```csharp\nvar x = 1;\n```\n\n> цитата\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
            Ui.FlushText();
            return 0;

        case "mailtest" when args.Length >= 2:
            // Проверка входа в почту: conexy mailtest name@yandex.ru (пароль приложения — в CONEXY_MAIL_PASSWORD).
            if (MailProvider.ForAddress(args[1]) is not { } testProvider)
            {
                Console.WriteLine("FAIL  неизвестный домен");
                return 1;
            }
            var mailTest = new MailConnector(new ConsoleApprover());
            var (mailOk, mailMessage) = await mailTest.LoginAsync(testProvider, args[1],
                Environment.GetEnvironmentVariable("CONEXY_MAIL_PASSWORD") ?? "wrong-password", CancellationToken.None);
            Console.WriteLine($"{(mailOk ? "PASS" : "FAIL")}  {mailMessage}");
            return mailOk ? 0 : 1;

        case "capturetest" when args.Length >= 3:
            // Проверка снимка окна: conexy capturetest <окно> <файл.png>
            var shot = await new WindowTools(new ConsoleApprover { AutoMode = true }, Environment.CurrentDirectory)
                .CaptureAsync(ToolArgs.Parse(System.Text.Json.JsonSerializer.Serialize(new { window = args[1] })), CancellationToken.None);
            var lines = shot.Output.Split('\n', 2);
            File.WriteAllBytes(args[2], Convert.FromBase64String(lines[0]["IMAGE_BASE64:image/png:".Length..]));
            Console.WriteLine(lines.ElementAtOrDefault(1));
            return 0;

        case "mcptest":
            return await SelfTest.McpAsync(args.Skip(1).Where(a => !a.StartsWith("--args=")).ToArray(),
                args.FirstOrDefault(a => a.StartsWith("--args="))?[7..]);

        case "downloadtest" when args.Length >= 4:
            return await Updater.TestDownload(args[1], args[2], args[3]) ? 0 : 1;

        case "timertest":
            // Проверка таймера долгой команды: строка инструмента, таймер после 10 с, итог «за N с».
            if (args.Contains("--ansi"))
                Ui.ForceAnsi();
            Ui.ToolCall("bash .\\gradlew build");
            await Task.Delay(TimeSpan.FromSeconds(13));
            Ui.Idle();
            Ui.ShowToolResult(ToolResult.Ok("BUILD SUCCESSFUL\nExit code: 0"));
            return 0;

        case "teamtest":
            // Проверка панели оркестра без сервера.
            if (args.Contains("--ansi"))
                Ui.ForceAnsi();
            Ui.SetProject(Directory.GetCurrentDirectory());
            Ui.TeamRoster([("worker-1", "backend", "API и база", "src/api"), ("worker-2", "frontend", "интерфейс", "src/ui"), ("worker-3", "qa", "тесты", "tests")]);
            Ui.Busy("Команда работает");
            Ui.AgentStatus("worker-1", "backend", "editing", "Правлю api/users.cs");
            Ui.AgentStatus("worker-2", "frontend", "running", "npm run build");
            Ui.AgentStatus("worker-3", "qa", "running", "dotnet test");
            await Task.Delay(1500);
            Ui.AgentStatus("worker-3", "qa", "idle", "Готово");
            await Task.Delay(1500);
            Ui.AgentStatus("worker-1", "backend", "idle", "Готово");
            Ui.AgentStatus("worker-2", "frontend", "idle", "Готово");
            Ui.Idle();
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

// WEB_DEVICE: добавлено 2026-10-18 — conexy projects [add <папка> [--name <имя>] | remove <id|имя>]
static int ProjectsCommand(string[] args)
{
    var store = ProjectStore.Load();
    var rest = args.SkipWhile(a => a != "projects").Skip(1).ToArray();
    switch (rest.FirstOrDefault()?.ToLowerInvariant())
    {
        case null or "list":
            if (store.Projects.Count == 0)
            {
                Console.WriteLine("Проектов нет. Добавьте: conexy projects add <папка> [--name <имя>]");
                return 0;
            }
            for (var i = 0; i < store.Projects.Count; i++)
            {
                var p = store.Projects[i];
                var missing = Directory.Exists(p.Path) ? string.Empty : "  (папка не найдена)";
                Console.WriteLine($"{(i == 0 ? "*" : " ")} {p.Name}  {p.Path}{missing}\n    id: {p.Id}");
            }
            Console.WriteLine("\n* — проект по умолчанию для демона.");
            return 0;

        case "add" when rest.Length >= 2 && !rest[1].StartsWith("--"):
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rest[1].Trim('"')));
            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine($"Папка не найдена: {path}");
                return 1;
            }
            if (Workspace.WhyUnsafe(path) is { } unsafeProject)
            {
                Console.Error.WriteLine($"Папка «{path}» не подходит: {unsafeProject}. Укажите папку проекта, а не системную.");
                return 1;
            }
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (store.Projects.FirstOrDefault(p => p.Path.Equals(path, cmp)) is { } existing)
            {
                Console.WriteLine($"Уже добавлен: {existing.Name} ({existing.Id})");
                return 0;
            }
            var name = Option(rest, "--name") ?? Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name))
                name = path;
            var project = new DeviceProject { Id = Guid.NewGuid().ToString(), Name = name, Path = path };
            store.Projects.Add(project);
            store.Save();
            Console.WriteLine($"Добавлен проект «{project.Name}» ({project.Id}).");
            if (Daemon.IsRunning())
                Console.WriteLine("Запущенный демон подхватит его в течение минуты.");
            return 0;

        case "remove" or "rm" when rest.Length >= 2:
            if (store.Find(rest[1]) is not { } target)
            {
                Console.Error.WriteLine($"Проект не найден: {rest[1]}");
                return 1;
            }
            store.Projects.Remove(target);
            store.Save();
            Console.WriteLine($"Удалён проект «{target.Name}».");
            return 0;

        default:
            Console.Error.WriteLine("Использование: conexy projects | projects add <папка> [--name <имя>] | projects remove <id|имя>");
            return 1;
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

    // WORKSPACE_GUARD: системная папка (бывает, если клиент запущен со странным cwd) — не рабочая область.
    if (Workspace.WhyUnsafe(root) is { } unsafeRoot)
    {
        if (Option(args, "--dir") is not null)
        {
            Console.Error.WriteLine($"Папка «{root}» не подходит: {unsafeRoot}. Укажите папку проекта.");
            return 1;
        }
        var safeRoot = Workspace.Suggest();
        Console.Error.WriteLine($"Рабочая папка «{root}» не подходит: {unsafeRoot}.");
        Console.Error.WriteLine($"Беру «{safeRoot}». Сменить: /cd <папка> или запуск с --dir <папка>.");
        root = safeRoot;
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
    // LOCAL_MCP: Google и локальные MCP-серверы живут весь сеанс (не перезапускаются при смене чата).
    await using var local = new LocalTools(approver);
    foreach (var line in await local.StartAsync(CancellationToken.None))
        Ui.Info("  " + line);
    var chatId = store.ChatFor(root, ChatIdFor(root));
    var firstBanner = true;
    while (true)
    {
        var next = await ChatInFolderAsync(server, approver, store, local, root, chatId, firstBanner);
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

static void DaemonCommand(string arg, string root)
{
    switch (arg.ToLowerInvariant())
    {
        case "on" or "install" or "--install":
            var (project, added) = ProjectStore.Load().Ensure(root);
            if (added)
                Ui.Info($"Папка «{project.Name}» добавлена в проекты: сайт сможет запускать в ней задачи.");
            Daemon.Install();
            Ui.Info("Можно закрывать это окно: демон работает в фоне, задачи и подтверждения — на сайте в режиме «Мой ПК».");
            break;
        case "off" or "uninstall" or "--uninstall":
            Daemon.Uninstall();
            break;
        case "" or "status":
            var projects = ProjectStore.Load().Projects;
            Ui.Info($"Демон: {(Daemon.IsRunning() ? "работает" : "не запущен")} · автозапуск: {(Daemon.AutostartEnabled() ? "включён" : "выключен")} · проектов: {projects.Count}");
            foreach (var p in projects)
                Ui.Info($"  {p.Name}  {Ui.Muted(p.Path)}");
            Ui.Info("/daemon on — работать в фоне и запускаться с Windows · /daemon off — остановить. Журнал: " + Daemon.LogPath);
            break;
        default:
            Ui.Info("/daemon [on|off|status]");
            break;
    }
}

// Справка-конспект: разделы, выровненная колонка команд, описание переносится по ширине окна,
// подкоманды — отдельными строками под командой.
static void PrintHelp()
{
    (string Title, (string Command, string Text)[] Items)[] sections =
    [
        ("Проект и файлы", [
            ("/ls [путь]", "содержимое папки, папки пронумерованы"),
            ("/cd <путь|N|..>", "сменить рабочую папку (N — номер из /ls)"),
            ("/pwd", "текущая папка"),
            ("/attach <файл>", "прикрепить файл или картинку; можно перетащить файл в окно или написать @путь"),
            ("/paste", "вложить из буфера обмена (или Ctrl+V); уходит вместе со следующим сообщением"),
            ("/detach [N]", "убрать вложение по номеру или все"),
            ("/rewind [N]", "откатить правки файлов за последние N ходов (команды терминала не откатываются)"),
        ]),
        ("Чат и агент", [
            ("/chats", "все чаты (общие с сайтом): переключиться или создать; у каждого своя папка"),
            ("/agent coder|cowork", "Coder — код и терминал; Cowork — исследования, документы, почта (платные тарифы)"),
            ("/compact [фокус]", "сжать длинный чат в пересказ и продолжить в новом — дешевле"),
            ("/usage", "тариф и расход токенов"),
        ]),
        ("Режимы работы", [
            ("/plan on|off", "агент только изучает проект и предлагает план; выполняет после вашего одобрения"),
            ("/team on|off", "оркестр: ведущий агент собирает команду субагентов (ProMax, Ultra, Admin); действует в чате до /team off"),
            ("/auto", "выполнять команды без подтверждения — вкл/выкл (или флаг --auto)"),
            ("/permissions", "команды без вопроса в этом проекте\nadd <команда> · remove N · clear"),
        ]),
        ("Фоновая работа с сайта", [
            ("/daemon on", "работать в фоне и запускаться вместе с Windows: задачи с сайта («Мой ПК») выполняются и при закрытом окне; текущая папка становится проектом"),
            ("/daemon off", "остановить и убрать из автозапуска"),
            ("/daemon", "состояние, проекты и журнал"),
        ]),
        ("Подключения", [
            ("/google login|logout", "Gmail, Календарь, Диск; отправка — только с подтверждения, токены только на этом ПК"),
            ("/mail login|logout", "Яндекс Почта или Mail.ru по паролю приложения; отправка — с подтверждения"),
            ("/mcp", "локальные MCP-серверы\ncatalog — готовые (Playwright, Context7, Sentry, Supabase, Postgres, Notion, Figma)\nadd <id> — из каталога · add <имя> <команда> — свой\nremove <имя> · reload · config"),
            ("/skill", "скиллы агента (SKILL.md), общие с сайтом\nadd <файл|папка> · remove <имя> · on|off <имя>"),
        ]),
        ("Прочее", [
            ("/update", "проверить обновления"),
            ("/help", "эта справка"),
            ("/exit", "выход (или exit)"),
        ]),
    ];

    var column = sections.SelectMany(s => s.Items).Max(i => i.Command.Length) + 4;
    int width;
    try { width = Math.Max(60, Console.IsOutputRedirected ? 100 : Console.WindowWidth - 1); }
    catch (IOException) { width = 100; }
    var textWidth = Math.Max(30, width - column);
    var indent = new string(' ', column);

    foreach (var (title, items) in sections)
    {
        Console.WriteLine();
        Console.WriteLine("  " + Ui.Bold(Ui.Accent(title)));
        foreach (var (command, text) in items)
        {
            var lines = text.Split('\n');
            var first = true;
            foreach (var (line, sub) in lines.Select((l, i) => (l, i > 0)))
            {
                var subStart = true;
                foreach (var chunk in Wrap(line, textWidth - (sub ? 2 : 0)))
                {
                    var left = first ? "  " + Ui.Accent(command) + new string(' ', column - 2 - command.Length) : indent;
                    Console.WriteLine(left + (sub ? Ui.Muted((subStart ? "› " : "  ") + chunk) : chunk));
                    first = false;
                    subStart = false;
                }
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine(Ui.Muted("  Всё остальное — задача для агента. Ctrl+C во время ответа — остановить."));
    Console.WriteLine(Ui.Muted("  Токен GitHub и MCP-серверы берутся из настроек на сайте автоматически."));
    Console.WriteLine(Ui.Muted("  Выделение текста: ведите мышью, Enter или правый клик — скопировать, Esc — отмена."));

    static IEnumerable<string> Wrap(string text, int max)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > max)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
            yield return line.ToString();
    }
}

// Возвращает новую пару (папка, чат), если пользователь переключился, или null для выхода.
static async Task<(string Root, Guid ChatId)?> ChatInFolderAsync(
    ServerClient server, ConsoleApprover approver, ChatStore store, LocalTools local, string root, Guid chatId, bool firstBanner)
{
    store.Bind(chatId, root);
    approver.ProjectRoot = root;
    Ui.PlanMode = false;
    // WEB_DEVICE: изменено 2026-10-18 — подтверждения задач с сайта уходят на сайт, своих ходов — в консоль.
    var device = new DeviceTools(daemon: false, approver,
        line => { if (ToolCallContext.Current is not { Remote: true }) Ui.ToolCall(line); },
        server.GetGitHubTokenAsync, local, root);
    var tools = new ToolRunner(root, device.Approver, Ui.ToolCall, server.GetGitHubTokenAsync, local);
    await using var session = new AgentSession(server, tools, chatId, device) { Local = local };
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
    Ui.SetProject(root);
    if (firstBanner && approver.AutoMode)
        Ui.Info("  Авто-режим включён: команды выполняются без подтверждения.");
    // WEB_DEVICE: добавлено 2026-10-18 — на сервере побеждает последняя регистрация устройства.
    if (firstBanner && Daemon.IsRunning())
        Ui.Info("  Демон conexy запущен: до конца этого сеанса задачи с сайта идут сюда, демон вернёт их себе в течение минуты после выхода.");
    Ui.Info($"  Чат {chatId.ToString()[..8]} · /chats — список чатов, /cd — сменить проект");
    await ShowHistoryAsync(server, chatId);
    var listed = new List<string>();
    var usage = await TryUsageAsync(server);
    Ui.SetUsage(usage);
    // Оркестр — настройка чата: включённый один раз, он работает до /team off (и переживает перезапуск).
    // Если тариф больше не позволяет — снимаем флаг.
    if (store.Team.Contains(chatId) && usage is { TeamAllowed: false })
        store.SetTeam(chatId, false);
    Ui.TeamMode = session.Orchestra = store.Team.Contains(chatId);
    // Агент — настройка чата; Cowork только на платном тарифе.
    if (store.ModeOf(chatId) == "cowork" && usage is { CoworkAllowed: false })
        store.SetMode(chatId, "coder");
    Ui.AgentMode = session.Mode = store.ModeOf(chatId);
    if (session.Orchestra)
        Ui.Info("  Оркестр агентов включён в этом чате · /team off — выключить");
    if (local.Google.Connected)
        Ui.Info($"  Google подключён: {local.Google.Email} · агент видит Gmail, Календарь и Диск");
    if (local.Mail.Connected)
        Ui.Info($"  Почта подключена: {local.Mail.Email} ({local.Mail.ProviderTitle})");

    while (true)
    {
        var prompt = Ui.ReadPrompt(approver.AutoMode, root);
        if (prompt is null || prompt.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(prompt) && Attachments.Pending.Count == 0)
            continue;

        // Команды терминала Windows, набранные внутри клиента («conexy daemon --install», «/ conexy …»).
        if (prompt.TrimStart('/', ' ').StartsWith("conexy ", StringComparison.OrdinalIgnoreCase))
        {
            var shellCommand = prompt.TrimStart('/', ' ')["conexy ".Length..].Trim();
            if (shellCommand.StartsWith("daemon", StringComparison.OrdinalIgnoreCase))
                prompt = shellCommand.Contains("--uninstall") ? "/daemon off" : shellCommand.Contains("--install") ? "/daemon on" : "/daemon";
            else
            {
                Ui.Info("Это команда для терминала Windows (cmd/PowerShell), а не для чата. Внутри conexy — команды со слешем: /help.");
                continue;
            }
        }

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
                        Ui.Info("  " + pasteMessage + (Attachments.Pending.Count > 0 ? " · допишите текст и нажмите Enter" : string.Empty));
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
                case "/agent":
                    var wantMode = arg.ToLowerInvariant() switch
                    {
                        "" => session.Mode == "coder" ? "cowork" : "coder",
                        "coder" or "code" or "кодер" => "coder",
                        "cowork" or "коворк" => "cowork",
                        _ => null,
                    };
                    if (wantMode is null)
                    {
                        Ui.Error("Используйте: /agent, /agent coder или /agent cowork");
                        break;
                    }
                    if (wantMode == "cowork")
                    {
                        usage ??= await TryUsageAsync(server);
                        if (usage is { CoworkAllowed: false })
                        {
                            Ui.Error($"Cowork доступен на платных тарифах (ваш тариф: {usage.Tier}). Подключить — на сайте.");
                            break;
                        }
                    }
                    Ui.AgentMode = session.Mode = wantMode;
                    store.SetMode(chatId, wantMode);
                    Ui.Info(wantMode == "cowork"
                        ? "Агент: Cowork — исследования, документы (.docx/.xlsx/.pptx сохраняются в папку), почта и сервисы. Терминала у Cowork нет."
                          + (session.Orchestra ? " Оркестр работает только в Coder." : string.Empty)
                        : "Агент: Coder — код, файлы и терминал.");
                    break;
                case "/google":
                    await GoogleCommandAsync(server, local, arg);
                    break;
                case "/mcp":
                    await McpCommandAsync(local, arg);
                    break;
                case "/mail":
                    await MailCommandAsync(local, arg);
                    break;
                case "/skill" or "/skills":
                    await SkillCommandAsync(server, root, arg);
                    break;
                case "/rewind":
                    await RewindCommandAsync(tools, arg);
                    break;
                case "/permissions":
                    PermissionsCommand(approver, root, arg);
                    break;
                case "/plan":
                    session.PlanMode = arg.ToLowerInvariant() switch
                    {
                        "on" or "вкл" => true,
                        "off" or "выкл" => false,
                        _ => !session.PlanMode,
                    };
                    Ui.PlanMode = session.PlanMode;
                    Ui.Info(session.PlanMode
                        ? "Режим плана: агент только изучает проект и предлагает план, правки и изменяющие команды заблокированы. После ответа можно одобрить план. /plan — выключить."
                        : "Режим плана выключен: агент снова может менять файлы.");
                    break;
                case "/compact":
                    if (await CompactCommandAsync(session, arg) is { } fresh)
                        return (root, fresh);
                    break;
                case "/team":
                    bool wantTeam;
                    switch (arg.ToLowerInvariant())
                    {
                        case "": wantTeam = !session.Orchestra; break;
                        case "on" or "вкл": wantTeam = true; break;
                        case "off" or "выкл": wantTeam = false; break;
                        default:
                            Ui.Error("Используйте: /team, /team on или /team off");
                            continue;
                    }
                    if (wantTeam)
                    {
                        usage ??= await TryUsageAsync(server);
                        if (usage is { TeamAllowed: false })
                        {
                            Ui.Error($"Оркестр агентов доступен на ProMax и Ultra (ваш тариф: {usage.Tier}).");
                            break;
                        }
                    }
                    if (wantTeam == session.Orchestra)
                    {
                        Ui.Info(wantTeam ? "Оркестр уже включён в этом чате." : "Оркестр уже выключен.");
                        break;
                    }
                    Ui.TeamMode = session.Orchestra = wantTeam;
                    store.SetTeam(chatId, wantTeam);
                    Ui.Info(wantTeam
                        ? "Оркестр включён: ведущий агент собирает команду субагентов. Работает в этом чате, пока не выключите: /team off"
                        : "Оркестр выключен: субагенты больше не запускаются, работает один агент.");
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
                case "/daemon":
                    DaemonCommand(arg, root);
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
            string? next = prompt;
            while (next is not null)
            {
                var error = await session.AskAsync(next, cts.Token);
                next = null;
                if (error is not null)
                {
                    Ui.Error(error);
                    break;
                }

                // Режим плана: план готов — человек решает, выполнять ли его (как «exit plan mode» в Claude Code).
                if (session.PlanMode && session.LastAnswer.Length > 0)
                {
                    var choice = await Ui.ChooseAsync("План готов", "Выполнить предложенный план?",
                        ["Да — выполнить план", "Нет — доработать план (напишите, что изменить)", "Выйти из режима плана"], cts.Token);
                    if (choice is 0 or 2)
                        Ui.PlanMode = session.PlanMode = false;
                    if (choice == 0)
                        next = "План одобрен. Выполни его полностью, шаг за шагом, и проверь результат.";
                }
            }
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

// /google: вход в Google (OAuth в браузере), выход и статус.
static async Task GoogleCommandAsync(ServerClient server, LocalTools local, string arg)
{
    switch (arg.ToLowerInvariant())
    {
        case "" or "status":
            Ui.Info(local.Google.Connected
                ? $"  Google подключён: {local.Google.Email}. Агент может читать почту, календарь и диск. /google logout — отключить."
                : "  Google не подключён. /google login — войти (откроется браузер).");
            break;
        case "login":
            var id = Environment.GetEnvironmentVariable("CONEXY_GOOGLE_CLIENT_ID");
            var secret = Environment.GetEnvironmentVariable("CONEXY_GOOGLE_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(id))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    var info = await server.GetClientInfoAsync(cts.Token);
                    (id, secret) = (info?.GoogleClientId, info?.GoogleClientSecret);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                Ui.Error("Вход в Google ещё не настроен на сервере. Можно указать свой OAuth-клиент (тип «Desktop app») в переменных CONEXY_GOOGLE_CLIENT_ID и CONEXY_GOOGLE_CLIENT_SECRET.");
                break;
            }
            var (ok, message) = await local.Google.LoginAsync(id.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret.Trim(), CancellationToken.None);
            if (ok)
                Ui.Info("  " + Ui.Green_(message) + Ui.Muted(" · попробуйте: «что нового в почте?»"));
            else
                Ui.Error(message);
            break;
        case "logout":
            await local.Google.LogoutAsync();
            Ui.Info("  Google отключён, доступ отозван.");
            break;
        default:
            Ui.Error("Используйте: /google, /google login или /google logout");
            break;
    }
}

// /mcp: локальные MCP-серверы из mcp.json.
static async Task McpCommandAsync(LocalTools local, string arg)
{
    var parts = arg.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    switch (parts.FirstOrDefault()?.ToLowerInvariant())
    {
        case null or "list":
            Console.WriteLine(Ui.Bold("MCP на этом ПК"));
            Console.WriteLine("  " + (local.Google.Connected
                ? Ui.Green_("●") + $" google (встроенный): {local.Google.Email} · {local.Google.Tools.Count} инструм."
                : Ui.Muted("○ google (встроенный): не подключён — /google login")));
            Console.WriteLine("  " + (local.Mail.Connected
                ? Ui.Green_("●") + $" mail (встроенный): {local.Mail.Email} · {local.Mail.Tools.Count} инструм."
                : Ui.Muted("○ mail (встроенный): Яндекс/Mail.ru не подключены — /mail login")));
            foreach (var s in local.Servers)
                Console.WriteLine("  " + (s.Error is null
                    ? Ui.Green_("●") + $" {s.Name}: {s.Tools.Count} инструм. " + Ui.Muted(string.Join(", ", s.Tools.Take(6).Select(t => t.Tool)) + (s.Tools.Count > 6 ? ", …" : string.Empty))
                    : Ui.Highlight("×") + $" {s.Name}: {s.Error}"));
            if (local.Servers.Count == 0)
                Ui.Info("  Своих серверов нет. Готовые для разработки: /mcp catalog");
            if (local.LimitWarning() is { } warning)
                Ui.Error(warning);
            Ui.Info($"  Конфиг: {McpConfig.FilePath}");
            break;
        case "catalog":
            var installed = McpConfig.Load().Servers;
            Console.WriteLine(Ui.Bold("Каталог MCP") + Ui.Muted(" · установить: /mcp add <id>"));
            foreach (var e in McpCatalog.Entries)
            {
                var has = installed.Values.Any(s => s.CatalogId == e.Id) || installed.ContainsKey(e.Id);
                Console.WriteLine($"  {(has ? Ui.Green_("●") : Ui.Muted("○"))} {Ui.Bold(e.Id),-12} {e.Title}");
                Console.WriteLine("      " + Ui.Muted(e.Description));
            }
            Ui.Info("  Нужен Node.js (команда npx). Серверы работают на вашем ПК, ключи хранятся зашифрованными.");
            break;
        case "add" when parts.Length == 2:
            if (McpCatalog.Find(parts[1]) is not { } entry)
            {
                Ui.Error($"В каталоге нет «{parts[1]}» (список: /mcp catalog). Свой сервер: /mcp add <имя> <команда ...>");
                break;
            }
            await InstallFromCatalogAsync(local, entry);
            break;
        case "add" when parts.Length == 3:
            var config = McpConfig.Load();
            var commandLine = SplitCommandLine(parts[2]);
            config.Servers[parts[1]] = new McpServerEntry { Command = commandLine[0], Args = commandLine.Skip(1).ToList() };
            config.Save();
            Ui.Info($"  Добавлен «{parts[1]}», запускаю…");
            foreach (var line in await local.StartAsync(CancellationToken.None))
                Ui.Info("  " + line);
            break;
        case "remove" or "rm" when parts.Length >= 2:
            var cfg = McpConfig.Load();
            if (!cfg.Servers.Remove(parts[1]))
            {
                Ui.Error($"Нет сервера «{parts[1]}».");
                break;
            }
            cfg.Save();
            await local.StartAsync(CancellationToken.None);
            Ui.Info($"  «{parts[1]}» удалён.");
            break;
        case "reload":
            foreach (var line in await local.StartAsync(CancellationToken.None, restart: true))
                Ui.Info("  " + line);
            if (local.Servers.Count == 0)
                Ui.Info("  В mcp.json нет серверов.");
            break;
        case "config":
            if (!File.Exists(McpConfig.FilePath))
                new McpConfig().Save();
            try { Process.Start(new ProcessStartInfo(McpConfig.FilePath) { UseShellExecute = true }); }
            catch (Exception) { /* путь напечатан ниже */ }
            Ui.Info($"  {McpConfig.FilePath} — после правки: /mcp reload");
            break;
        default:
            Ui.Error("Используйте: /mcp, /mcp catalog, /mcp add <id>, /mcp add <имя> <команда аргументы...>, /mcp remove <имя>, /mcp reload, /mcp config");
            break;
    }
}

// /mcp add <id>: сервер из каталога — спрашиваем ключи, шифруем их и запускаем.
static async Task InstallFromCatalogAsync(LocalTools local, CatalogEntry entry)
{
    if (entry.Command == "npx" && OperatingSystem.IsWindows() && StdioMcpClient.FindNpxCli() is null)
    {
        Ui.Error("Не найден Node.js (npx). Установите: winget install OpenJS.NodeJS.LTS — или попросите агента «установи Node.js», затем повторите.");
        return;
    }

    Console.WriteLine(Ui.Bold(entry.Title) + " — " + entry.Description);
    var server = new McpServerEntry
    {
        Command = entry.Command,
        Args = entry.Args.ToList(),
        Env = entry.Env is null ? null : new Dictionary<string, string>(entry.Env),
        CatalogId = entry.Id,
    };
    foreach (var input in entry.Inputs ?? [])
    {
        Ui.Info("  " + input.Help);
        Console.Write($"  {input.Prompt}: ");
        var value = (input.Secret ? ReadSecret() : Console.ReadLine() ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            if (input.Optional)
                continue;
            Ui.Error("Без этого сервер не заработает — установка отменена.");
            return;
        }
        server.SetSecret(input.Name, value);
        if (input.ArgIfSet is { } arg)
            server.Args!.Add(arg);
    }

    var config = McpConfig.Load();
    config.Servers[entry.Id] = server;
    config.Save();
    Ui.Info($"  Добавлен «{entry.Id}», запускаю (первый запуск скачивает пакет — до минуты)…");
    // Перезапуск именно этого сервера, если он уже был запущен со старыми ключами.
    var restart = local.Servers.Any(s => s.Name == McpConfig.Slug(entry.Id));
    foreach (var line in await local.StartAsync(CancellationToken.None, restart))
        Ui.Info("  " + line);
    if (entry.Note is { } note)
        Ui.Info("  " + Ui.Muted(note));
}

// /skill: скиллы агента (SKILL.md). Хранятся на сервере — те же, что на сайте («+» → «Скиллы»).
static async Task SkillCommandAsync(ServerClient server, string root, string arg)
{
    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var command = parts.FirstOrDefault()?.ToLowerInvariant();
    var rest = parts.Length > 1 ? parts[1].Trim().Trim('"', '\'') : string.Empty;
    try
    {
        // «/skill <путь>» без слова add — тоже добавление (удобно перетащить файл в окно).
        if (command is not (null or "list" or "add" or "remove" or "rm" or "delete" or "on" or "off" or "show"))
        {
            rest = arg.Trim().Trim('"', '\'');
            command = "add";
        }

        switch (command)
        {
            case null or "list":
            {
                var skills = await server.GetSkillsAsync(CancellationToken.None);
                Console.WriteLine(Ui.Bold("Скиллы агента") + Ui.Muted(" · общие с сайтом"));
                if (skills.Count == 0)
                    Ui.Info("  Пока нет. Добавить: /skill add <путь к SKILL.md или папке со скиллом> (можно перетащить файл в окно)");
                foreach (var s in skills)
                {
                    var description = s.Description.Length > 90 ? s.Description[..90] + "…" : s.Description;
                    Console.WriteLine($"  {(s.Enabled ? Ui.Green_("●") : Ui.Muted("○"))} {Ui.Bold(s.Name)} " + Ui.Muted("— " + description));
                }
                if (skills.Count > 0)
                    Ui.Info("  " + Ui.Muted("Агент сам берёт нужный скилл по описанию. /skill off <имя> — выключить, /skill remove <имя> — удалить"));
                break;
            }
            case "add":
            {
                if (rest.Length == 0)
                {
                    Ui.Error("Укажите файл: /skill add C:\\skills\\review\\SKILL.md (или папку, где лежит SKILL.md)");
                    break;
                }
                var path = Path.IsPathRooted(rest) ? rest : Path.Combine(root, rest);
                // Папка скилла в формате Claude: внутри SKILL.md.
                if (Directory.Exists(path))
                {
                    var inner = Path.Combine(path, "SKILL.md");
                    if (!File.Exists(inner))
                    {
                        Ui.Error($"В папке нет SKILL.md: {path}");
                        break;
                    }
                    path = inner;
                }
                if (!File.Exists(path))
                {
                    Ui.Error($"Файл не найден: {path}");
                    break;
                }
                if (new FileInfo(path).Length > 256 * 1024)
                {
                    Ui.Error("Файл скилла больше 256 КБ.");
                    break;
                }
                // Для SKILL.md без имени во frontmatter подсказываем серверу имя папки скилла.
                var uploadName = Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                    ? (Path.GetFileName(Path.GetDirectoryName(path)) ?? "skill") + ".md"
                    : Path.GetFileName(path);
                var (skill, error) = await server.UploadSkillAsync(uploadName, await File.ReadAllBytesAsync(path), CancellationToken.None);
                if (skill is null)
                {
                    Ui.Error("Скилл не добавлен: " + error);
                    break;
                }
                Ui.Info("  " + Ui.Green_($"Скилл «{skill.Name}» добавлен") + Ui.Muted($" — {skill.Description}"));
                Ui.Info("  " + Ui.Muted("Агент применит его, когда задача подойдёт под описание. Доступен и на сайте."));
                break;
            }
            case "remove" or "rm" or "delete" or "on" or "off":
            {
                var skills = await server.GetSkillsAsync(CancellationToken.None);
                var skill = skills.FirstOrDefault(s => s.Name.Equals(rest, StringComparison.OrdinalIgnoreCase));
                if (skill is null)
                {
                    Ui.Error($"Нет скилла «{rest}». Список: /skill");
                    break;
                }
                if (command is "on" or "off")
                {
                    await server.SetSkillEnabledAsync(skill.Id, command == "on", CancellationToken.None);
                    Ui.Info($"  «{skill.Name}» {(command == "on" ? "включён" : "выключен — агент его не увидит")}.");
                }
                else
                {
                    await server.DeleteSkillAsync(skill.Id, CancellationToken.None);
                    Ui.Info($"  Скилл «{skill.Name}» удалён.");
                }
                break;
            }
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthRejectedException or IOException)
    {
        Ui.Error("Скиллы недоступны: " + ex.Message + " (нужен обновлённый сервер)");
    }
}

// /rewind [N]: откат файлов проекта к состоянию до последних ходов агента.
static async Task RewindCommandAsync(ToolRunner tools, string arg)
{
    var turns = tools.Checkpoints.Turns;
    if (turns.Count == 0)
    {
        Ui.Info("  Откатывать нечего: в этой сессии агент не менял файлы проекта.");
        return;
    }

    int count;
    if (int.TryParse(arg, out var n) && n >= 1)
    {
        count = Math.Min(n, turns.Count);
    }
    else
    {
        // Список с новых: выбор k откатывает k последних ходов.
        var recent = turns.Reverse().Take(9).ToList();
        var options = recent.Select((t, i) =>
            $"до хода «{t.Prompt}» ({t.At:HH:mm}, файлов: {t.Originals.Count}){(i > 0 ? $" — отменит {i + 1} хода" : string.Empty)}").ToArray();
        var picked = await Ui.ChooseAsync("Откат изменений",
            "К какому моменту вернуть файлы проекта? Откатываются правки агента; изменения от команд (npm install, сборка, git) не откатываются.",
            options, CancellationToken.None);
        if (picked < 0)
        {
            Ui.Info("  Отменено.");
            return;
        }
        count = picked + 1;
    }

    var undone = turns.Reverse().Take(count).Select(t => t.Prompt).ToList();
    var (restored, deleted, failed, untracked) = tools.Checkpoints.Rewind(count);
    Ui.Info("  " + Ui.Green_($"Откачено ходов: {count}") + $" · восстановлено файлов: {restored}, удалено созданных: {deleted}");
    if (failed.Count > 0)
        Ui.Error("Не удалось вернуть (заняты другой программой?): " + string.Join(", ", failed.Select(Path.GetFileName)));
    if (untracked.Count > 0)
        Ui.Info("  " + Ui.Muted("Слишком большие для снимка и не откачены: " + string.Join(", ", untracked.Select(Path.GetFileName))));
    // Агент помнит свои правки — предупреждаем его в следующем сообщении, иначе он будет считать их на месте.
    AgentSession.PendingNote = "[Пользователь откатил (/rewind) все твои правки файлов из ходов: "
                               + string.Join("; ", undone.Select(p => $"«{p}»")) + ". Файлы снова в состоянии до них — перечитай их, прежде чем снова править.]";
}

// /permissions: постоянные разрешения команд для текущего проекта.
static void PermissionsCommand(ConsoleApprover approver, string root, string arg)
{
    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    switch (parts.FirstOrDefault()?.ToLowerInvariant())
    {
        case null or "list":
            var rules = approver.Permissions.RulesFor(root);
            Console.WriteLine(Ui.Bold("Разрешённые без вопроса команды") + Ui.Muted(" · " + root));
            if (rules.Count == 0)
                Ui.Info("  Пока нет. При подтверждении команды выберите «Да, и всегда разрешать…» или: /permissions add npm test");
            for (var i = 0; i < rules.Count; i++)
                Console.WriteLine($"  {i + 1}. {rules[i]}…");
            Ui.Info("  " + Ui.Muted("Работают только для одиночных команд без ; | && > — цепочки всё равно спрашиваются. /permissions remove N, /permissions clear"));
            break;
        case "add" when parts.Length == 2:
            if (PermissionStore.SuggestRule(parts[1]) is null)
            {
                Ui.Error("Такое правило нельзя: только одиночная команда без цепочек и не запрещённая политикой.");
                break;
            }
            approver.Permissions.Add(root, parts[1].Trim());
            Ui.Info($"  Добавлено: «{parts[1].Trim()}…»");
            break;
        case "remove" or "rm" when parts.Length == 2 && int.TryParse(parts[1], out var index):
            Ui.Info(approver.Permissions.Remove(root, index - 1) ? "  Удалено." : "  Нет правила с таким номером.");
            break;
        case "clear":
            approver.Permissions.Clear(root);
            Ui.Info("  Все правила этого проекта удалены.");
            break;
        default:
            Ui.Error("Используйте: /permissions, /permissions add <команда>, /permissions remove N, /permissions clear");
            break;
    }
}

// /compact: сжать длинный чат — агент пишет пересказ, работа продолжается в новом чате с этим пересказом.
// Дешевле и точнее, чем тащить сотни сообщений истории в каждый запрос.
static async Task<Guid?> CompactCommandAsync(AgentSession session, string focus)
{
    Ui.Info("  Сжимаю чат: агент составит пересказ, дальше работа продолжится в новом чате с этим пересказом.");
    var wasPlan = session.PlanMode;
    session.PlanMode = false;
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        var error = await session.AskAsync(
            "Составь сжатый пересказ ВСЕГО этого чата, чтобы продолжить работу в новом чате без потерь: цель, принятые решения, " +
            "изменённые файлы, команды сборки и запуска, текущее состояние, открытые проблемы и следующие шаги. " +
            "Ничего не делай и инструменты не вызывай — только пересказ, без вступлений."
            + (focus.Length > 0 ? " Особое внимание: " + focus : string.Empty),
            cts.Token);
        if (error is not null || session.LastAnswer.Trim().Length < 40)
        {
            Ui.Error("Не получилось составить пересказ — остаёмся в этом чате." + (error is null ? string.Empty : " " + error));
            session.PlanMode = wasPlan;
            return null;
        }
        AgentSession.PendingNote = "[Продолжение прошлого чата. Сжатый пересказ всего, что было сделано и решено:]\n"
                                   + session.LastAnswer.Trim() + "\n[Конец пересказа.]";
        Ui.Info("  " + Ui.Green_("Чат сжат.") + " Пересказ уйдёт агенту вместе с вашим следующим сообщением. Старый чат остался в /chats.");
        return Guid.NewGuid();
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

// /mail: Яндекс Почта / Mail.ru по IMAP и SMTP с паролем приложения.
static async Task MailCommandAsync(LocalTools local, string arg)
{
    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    switch (parts.FirstOrDefault()?.ToLowerInvariant())
    {
        case null or "status":
            Ui.Info(local.Mail.Connected
                ? $"  Почта подключена: {local.Mail.Email} ({local.Mail.ProviderTitle}). Агент ищет и читает письма, скачивает вложения; отправка — с вашего подтверждения. /mail logout — отключить."
                : "  Почта не подключена. /mail login — Яндекс Почта или Mail.ru (нужен пароль приложения).");
            break;
        case "login":
            Console.Write("  Адрес почты: ");
            var email = (Console.ReadLine() ?? string.Empty).Trim();
            if (!email.Contains('@'))
            {
                Ui.Error("Нужен адрес вида name@yandex.ru или name@mail.ru.");
                break;
            }
            var provider = (parts.Length > 1 ? MailProvider.Find(parts[1]) : null) ?? MailProvider.ForAddress(email);
            if (provider is null)
            {
                Console.Write("  Почта на своём домене — чей сервис? [y] Яндекс 360 / [m] Mail.ru для бизнеса: ");
                provider = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant() switch
                {
                    "y" or "н" or "yandex" => MailProvider.Find("yandex"),
                    "m" or "ь" or "mail" => MailProvider.Find("mailru"),
                    _ => null,
                };
                if (provider is null)
                {
                    Ui.Error("Пока поддерживаются Яндекс и Mail.ru. Для Gmail — /google login.");
                    break;
                }
            }
            Ui.Info($"  {provider.Title}: нужен пароль приложения, не обычный пароль. {provider.PasswordHelp}");
            Console.Write("  Пароль приложения (символы не отображаются): ");
            // Яндекс показывает пароль группами через пробел — пробелы убираем.
            var password = ReadSecret().Replace(" ", string.Empty);
            if (password.Length == 0)
            {
                Ui.Error("Пароль не введён.");
                break;
            }
            Ui.Info("  Проверяю вход…");
            var (ok, message) = await local.Mail.LoginAsync(provider, email, password, CancellationToken.None);
            if (ok)
                Ui.Info("  " + Ui.Green_(message) + Ui.Muted(" · попробуйте: «что нового в почте?»"));
            else
                Ui.Error(message);
            break;
        case "logout":
            local.Mail.Logout();
            Ui.Info("  Почта отключена. Пароль приложения удалён с этого ПК; в настройках почты его можно отозвать.");
            break;
        default:
            Ui.Error("Используйте: /mail, /mail login или /mail logout");
            break;
    }
}

static List<string> SplitCommandLine(string line)
{
    var result = new List<string>();
    var current = new StringBuilder();
    var quoted = false;
    foreach (var c in line)
    {
        if (c == '"') { quoted = !quoted; continue; }
        if (c == ' ' && !quoted)
        {
            if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            continue;
        }
        current.Append(c);
    }
    if (current.Length > 0)
        result.Add(current.ToString());
    return result;
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

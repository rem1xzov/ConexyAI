namespace ConexyAI.Desktop.Mcp;

/// <summary>Что спросить у пользователя при установке сервера из каталога.</summary>
/// <param name="Name">Имя секрета/параметра, на него ссылаются ${secret:Name} в Args/Env.</param>
/// <param name="Prompt">Вопрос в терминале.</param>
/// <param name="Help">Где это взять.</param>
/// <param name="Secret">Скрывать ввод и хранить зашифрованно.</param>
/// <param name="Optional">Можно пропустить (Enter); тогда <see cref="ArgIfSet"/> не добавляется.</param>
/// <param name="ArgIfSet">Аргумент, который добавляется, только если значение введено.</param>
/// <param name="Pattern">Регулярное выражение, которому должно соответствовать значение (адреса подставляются в URL — нельзя пускать мусор).</param>
public sealed record CatalogInput(string Name, string Prompt, string Help, bool Secret = true, bool Optional = false, string? ArgIfSet = null, string? Pattern = null);

/// <param name="Category">Раздел каталога (для группировки в /mcp catalog).</param>
/// <param name="Community">Пакет написан не самим сервисом, а сообществом: версия закреплена, но код стоит просмотреть.</param>
/// <param name="StartupSeconds">Сколько ждать первого запуска (удалённым серверам нужно время на вход в браузере).</param>
public sealed record CatalogEntry(
    string Id,
    string Title,
    string Description,
    string Command,
    string[] Args,
    Dictionary<string, string>? Env = null,
    CatalogInput[]? Inputs = null,
    string? Note = null,
    string Category = "Разработка",
    bool Community = false,
    int StartupSeconds = 0);

/// <summary>
/// Проверенные MCP-серверы: ставятся одной командой /mcp add &lt;id&gt;. Версии закреплены — сервер получает ключи
/// пользователя и работает на его ПК, поэтому «latest» сюда не подходит. Обновлять версии — вручную после проверки.
/// Размещённые у самих сервисов (Notion, Linear, Atlassian, Figma, Slack, DaData …) подключаются через mcp-remote:
/// вход в браузере, ключи вводить не нужно.
/// </summary>
public static class McpCatalog
{
    private const string ForeignNote =
        "Сервис зарубежный: из России может не открываться без VPN — тогда инструменты вернут ошибку сети.";

    private const string CommunityNote =
        "Сторонний пакет (написан не самим сервисом): версия закреплена, но код стоит просмотреть. Выдайте токену минимальные права.";

    private const string OAuthNote =
        "При первом запуске откроется браузер — войдите в сервис (на вход даётся ~4 минуты; не успели — /mcp reload). " +
        "Доступ хранится на этом ПК, ключи вводить не нужно.";

    /// <summary>Мост stdio ↔ удалённый MCP по HTTP с OAuth, закреплённая версия.</summary>
    public const string RemoteBridge = "mcp-remote@0.15.2";

    private static CatalogEntry Remote(
        string id, string title, string description, string url, string category,
        string? note = null, CatalogInput[]? inputs = null, string[]? extraArgs = null,
        Dictionary<string, string>? env = null, bool community = false) =>
        new(id, title, description, "npx", ["-y", RemoteBridge, url, .. extraArgs ?? []],
            Env: env, Inputs: inputs, Note: note ?? OAuthNote, Category: category, Community: community, StartupSeconds: 240);

    private static CatalogEntry Npx(
        string id, string title, string description, string package, string category,
        string[]? args = null, Dictionary<string, string>? env = null, CatalogInput[]? inputs = null,
        string? note = null, bool community = false, int startup = 0) =>
        new(id, title, description, "npx", ["-y", package, .. args ?? []],
            Env: env, Inputs: inputs, Note: note, Category: category, Community: community, StartupSeconds: startup);

    private static string[] Header(string name) => ["--header", $"{name}:" + "${AUTH_HEADER}"];

    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        // ── Разработка ───────────────────────────────────────────────────────
        new("playwright", "Playwright (Microsoft)",
            "Браузер под управлением агента: открыть страницу, кликнуть, ввести текст, прочитать консоль — проверка интерфейса как живым пользователем.",
            "npx", ["-y", "@playwright/mcp@0.0.83", "--browser", OperatingSystem.IsWindows() ? "msedge" : "chrome"],
            Note: "Окно браузера откроется на вашем ПК. Инструментов много (~20) — если упрётесь в лимит, агент переключится на поиск по инструментам."),

        Npx("chrome-devtools", "Chrome DevTools (Google)",
            "Отладка страниц как в DevTools: сеть, консоль, производительность, скриншоты, DOM — агент видит, почему сайт тормозит или ломается.",
            "chrome-devtools-mcp@1.12.0", "Разработка",
            note: "Нужен установленный Google Chrome."),

        new("context7", "Context7 (Upstash)",
            "Актуальная документация и примеры для любых библиотек и фреймворков — агент меньше ошибается в API новых версий.",
            "npx", ["-y", "@upstash/context7-mcp@4.4.2"],
            Inputs:
            [
                new("CONTEXT7_API_KEY", "API-ключ Context7 (необязательно, Enter — пропустить)",
                    "Без ключа работает с ограничением запросов; ключ — бесплатно на context7.com/dashboard.",
                    Optional: true, ArgIfSet: "--api-key=${secret:CONTEXT7_API_KEY}"),
            ],
            Note: ForeignNote),

        Remote("deepwiki", "DeepWiki (Cognition)",
            "Документация и разбор любого публичного репозитория GitHub: архитектура, ответы на вопросы по коду. Без ключа.",
            "https://mcp.deepwiki.com/mcp", "Разработка", note: ForeignNote),

        Remote("github", "GitHub (официальный)",
            "Репозитории, issues, pull request, Actions, code scanning, проекты — весь GitHub через официальный сервер. Агент и без него умеет клонировать и пушить; этот добавляет API.",
            "https://api.githubcopilot.com/mcp/", "Разработка",
            inputs: [new("GITHUB_PAT", "Personal access token GitHub", "github.com/settings/tokens → fine-grained token с нужными правами (чтение — Metadata, Issues, Pull requests).")],
            extraArgs: Header("Authorization"), env: new() { ["AUTH_HEADER"] = "Bearer ${secret:GITHUB_PAT}" },
            note: ForeignNote),

        new("sentry", "Sentry",
            "Ошибки и падения из продакшена: агент читает стектрейсы, события и issues и сразу чинит код.",
            "npx", ["-y", "@sentry/mcp-server@0.42.0", "--access-token=${secret:SENTRY_TOKEN}"],
            Inputs:
            [
                new("SENTRY_TOKEN", "User Auth Token Sentry",
                    "sentry.io → User Settings → Personal Tokens (User Auth Tokens) → Create New Token; права: org:read, project:read, event:read."),
                new("SENTRY_HOST", "Адрес своего Sentry (только self-hosted, Enter — sentry.io)",
                    "Например sentry.mycompany.ru.", Secret: false, Optional: true, ArgIfSet: "--host=${secret:SENTRY_HOST}"),
            ],
            Note: "sentry.io из России может быть недоступен без VPN; self-hosted Sentry работает напрямую."),

        Remote("linear", "Linear",
            "Задачи, проекты и циклы Linear: агент читает, создаёт и обновляет issues.",
            "https://mcp.linear.app/mcp", "Разработка", note: OAuthNote + " " + ForeignNote),

        Remote("atlassian", "Atlassian (Jira и Confluence)",
            "Задачи Jira и страницы Confluence: поиск, чтение, создание и обновление.",
            "https://mcp.atlassian.com/v1/sse", "Разработка", note: OAuthNote + " " + ForeignNote),

        Remote("asana", "Asana",
            "Задачи, проекты и портфели Asana.",
            "https://mcp.asana.com/sse", "Разработка", note: OAuthNote + " " + ForeignNote),

        Remote("vercel", "Vercel",
            "Проекты и деплои Vercel: логи сборок, статус, документация.",
            "https://mcp.vercel.com", "Облака и инфраструктура", note: OAuthNote + " " + ForeignNote),

        Remote("cloudflare-docs", "Cloudflare (документация)",
            "Актуальная документация Cloudflare: Workers, Pages, R2, D1. Без ключа.",
            "https://docs.mcp.cloudflare.com/mcp", "Облака и инфраструктура", note: ForeignNote),

        Remote("neon", "Neon (Postgres)",
            "Базы Neon: проекты, ветки, схема и SQL-запросы.",
            "https://mcp.neon.tech/mcp", "Данные", note: OAuthNote + " " + ForeignNote),

        Remote("postman", "Postman",
            "Коллекции и окружения Postman: запуск запросов и тестов API.",
            "https://mcp.postman.com/mcp", "Разработка", note: OAuthNote + " " + ForeignNote),

        Npx("kubernetes", "Kubernetes",
            "Кластеры Kubernetes: поды, деплойменты, логи, события. Разрушающие операции выключены.",
            "mcp-server-kubernetes@4.2.1", "Облака и инфраструктура",
            env: new() { ["ALLOW_ONLY_NON_DESTRUCTIVE_TOOLS"] = "true" },
            note: "Работает через ваш kubeconfig (kubectl должен быть настроен). Удаление и scale отключены — это можно изменить в /mcp config.", community: true),

        new("supabase", "Supabase",
            "База, схема, логи и миграции проекта Supabase. Подключается только на чтение.",
            "npx", ["-y", "@supabase/mcp-server-supabase@0.14.0", "--read-only"],
            Env: new() { ["SUPABASE_ACCESS_TOKEN"] = "${secret:SUPABASE_ACCESS_TOKEN}" },
            Inputs:
            [
                new("SUPABASE_ACCESS_TOKEN", "Personal Access Token Supabase",
                    "supabase.com/dashboard/account/tokens → Generate new token."),
                new("SUPABASE_PROJECT", "Project ref (Enter — все проекты аккаунта)",
                    "Settings → General → Project ID. С ним агент видит только этот проект — так безопаснее.",
                    Secret: false, Optional: true, ArgIfSet: "--project-ref=${secret:SUPABASE_PROJECT}"),
            ],
            Note: "Режим только чтения: агент не изменит данные и схему. Убрать --read-only можно в /mcp config.", Category: "Данные"),

        new("postgres", "PostgreSQL (только чтение)",
            "Схема таблиц и SELECT-запросы к вашей базе Postgres (локальной или удалённой).",
            "npx", ["-y", "@modelcontextprotocol/server-postgres@0.6.2", "${secret:DATABASE_URL}"],
            Inputs:
            [
                new("DATABASE_URL", "Строка подключения (postgresql://user:pass@host:5432/db)",
                    "Лучше отдельный пользователь БД только с правами SELECT."),
            ],
            Note: "Официальный сервер Anthropic, сейчас в архиве (не обновляется), зато простой и только на чтение.", Category: "Данные"),

        Npx("mysql", "MySQL / MariaDB",
            "Схема и SELECT-запросы к базе MySQL или MariaDB. Запись выключена.",
            "@benborla29/mcp-server-mysql@2.0.9", "Данные",
            env: new()
            {
                ["MYSQL_HOST"] = "${secret:MYSQL_HOST}",
                ["MYSQL_PORT"] = "${secret:MYSQL_PORT}",
                ["MYSQL_USER"] = "${secret:MYSQL_USER}",
                ["MYSQL_PASS"] = "${secret:MYSQL_PASS}",
                ["MYSQL_DB"] = "${secret:MYSQL_DB}",
            },
            inputs:
            [
                new("MYSQL_HOST", "Хост", "Например localhost или адрес сервера БД.", Secret: false),
                new("MYSQL_PORT", "Порт", "Обычно 3306.", Secret: false),
                new("MYSQL_USER", "Пользователь", "Лучше отдельный пользователь только с правом SELECT.", Secret: false),
                new("MYSQL_PASS", "Пароль", "Пароль пользователя БД."),
                new("MYSQL_DB", "База данных", "Имя базы.", Secret: false),
            ],
            note: "INSERT/UPDATE/DELETE выключены по умолчанию в самом сервере.", community: true),

        Npx("memory", "Память (граф знаний)",
            "Долгая память агента на вашем ПК: факты, сущности и связи между ними сохраняются между сеансами.",
            "@modelcontextprotocol/server-memory@2.0.0", "Служебные"),

        Npx("sequential-thinking", "Последовательное мышление",
            "Пошаговое рассуждение для сложных задач: агент разбивает проблему на шаги и пересматривает их.",
            "@modelcontextprotocol/server-sequential-thinking@2.0.0", "Служебные"),

        // ── Дизайн и документы ───────────────────────────────────────────────
        new("figma", "Figma (Framelink)",
            "Макеты Figma для вёрстки: агент получает структуру, размеры, цвета и тексты фрейма по ссылке.",
            "npx", ["-y", "figma-developer-mcp@0.13.2", "--stdio"],
            Env: new() { ["FIGMA_API_KEY"] = "${secret:FIGMA_API_KEY}" },
            Inputs:
            [
                new("FIGMA_API_KEY", "Personal access token Figma",
                    "Figma → Settings → Security → Personal access tokens → Generate new token (права на чтение файлов)."),
            ],
            Note: ForeignNote, Category: "Дизайн и документы"),

        Remote("figma-official", "Figma (официальный)",
            "Официальный сервер Figma: макеты, компоненты, переменные дизайн-системы, Code Connect — вход в браузере, без токена.",
            "https://mcp.figma.com/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Remote("figma-desktop", "Figma Dev Mode (локальный)",
            "Выделите фрейм в приложении Figma — агент увидит именно его. Работает через приложение Figma на этом ПК.",
            "http://127.0.0.1:3845/mcp", "Дизайн и документы",
            note: "Нужно приложение Figma с включённым Dev Mode MCP Server (Preferences → Enable Dev Mode MCP Server) и открытый файл.",
            extraArgs: ["--allow-http"]),

        new("notion", "Notion (по токену)",
            "Страницы и базы Notion: ТЗ, заметки, задачи — агент читает и обновляет их.",
            "npx", ["-y", "@notionhq/notion-mcp-server@2.5.2"],
            Env: new() { ["NOTION_TOKEN"] = "${secret:NOTION_TOKEN}" },
            Inputs:
            [
                new("NOTION_TOKEN", "Internal Integration Secret (ntn_…)",
                    "notion.so/profile/integrations → New integration → Internal; затем на нужных страницах «…» → Connections → добавить интеграцию."),
            ],
            Note: ForeignNote, Category: "Дизайн и документы"),

        Remote("notion-oauth", "Notion (вход в браузере)",
            "Тот же Notion без ручных токенов: вход через браузер, доступ к тем страницам, которые разрешите.",
            "https://mcp.notion.com/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Npx("airtable", "Airtable",
            "Базы и таблицы Airtable: чтение, создание и обновление записей.",
            "airtable-mcp-server@1.14.0", "Дизайн и документы",
            env: new() { ["AIRTABLE_API_KEY"] = "${secret:AIRTABLE_API_KEY}" },
            inputs: [new("AIRTABLE_API_KEY", "Personal access token Airtable", "airtable.com/create/tokens → права data.records:read/write и schema.bases:read.")],
            note: ForeignNote, community: true),

        Remote("dropbox", "Dropbox",
            "Файлы Dropbox: поиск, чтение, загрузка.",
            "https://mcp.dropbox.com/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Remote("box", "Box",
            "Файлы и папки Box.",
            "https://mcp.box.com", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Remote("monday", "monday.com",
            "Доски, элементы и обновления monday.com.",
            "https://mcp.monday.com/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        // ── Мессенджеры ──────────────────────────────────────────────────────
        Remote("slack", "Slack (официальный)",
            "Каналы, сообщения и поиск по Slack: вход в браузере, доступ только к тому, что разрешите.",
            "https://mcp.slack.com/mcp", "Мессенджеры", note: OAuthNote + " " + ForeignNote),

        Npx("slack-bot", "Slack (бот-токен)",
            "Slack через токен своего бота: отправка сообщений, ответы в тредах, реакции, чтение каналов.",
            "@modelcontextprotocol/server-slack@2025.4.25", "Мессенджеры",
            env: new()
            {
                ["SLACK_BOT_TOKEN"] = "${secret:SLACK_BOT_TOKEN}",
                ["SLACK_TEAM_ID"] = "${secret:SLACK_TEAM_ID}",
            },
            inputs:
            [
                new("SLACK_BOT_TOKEN", "Bot User OAuth Token (xoxb-…)", "api.slack.com/apps → ваше приложение → OAuth & Permissions."),
                new("SLACK_TEAM_ID", "ID рабочего пространства (T…)", "Открывается в адресе Slack в браузере.", Secret: false),
            ],
            note: "Официальный сервер Anthropic, сейчас в архиве. " + ForeignNote),

        Npx("discord", "Discord",
            "Серверы и каналы Discord от имени вашего бота: отправка сообщений, чтение, управление ролями и каналами.",
            "@pasympa/discord-mcp@2.2.0", "Мессенджеры",
            env: new() { ["DISCORD_TOKEN"] = "${secret:DISCORD_TOKEN}" },
            inputs: [new("DISCORD_TOKEN", "Токен бота Discord", "discord.com/developers/applications → ваше приложение → Bot → Reset Token; бота нужно пригласить на сервер.")],
            note: CommunityNote + " " + ForeignNote, community: true),

        // ── Поиск и веб ──────────────────────────────────────────────────────
        Npx("tavily", "Tavily (поиск)",
            "Веб-поиск и извлечение страниц, заточенные под агентов: свежие данные с источниками.",
            "tavily-mcp@0.2.22", "Поиск и веб",
            env: new() { ["TAVILY_API_KEY"] = "${secret:TAVILY_API_KEY}" },
            inputs: [new("TAVILY_API_KEY", "API-ключ Tavily", "app.tavily.com → API Keys (есть бесплатный лимит).")],
            note: ForeignNote),

        Npx("brave-search", "Brave Search",
            "Веб-, новостной, видео- и картиночный поиск Brave.",
            "@brave/brave-search-mcp-server@2.1.4", "Поиск и веб",
            env: new() { ["BRAVE_API_KEY"] = "${secret:BRAVE_API_KEY}" },
            inputs: [new("BRAVE_API_KEY", "API-ключ Brave Search", "brave.com/search/api → получить ключ (есть бесплатный план).")],
            note: ForeignNote),

        Npx("firecrawl", "Firecrawl",
            "Вытаскивает чистый текст и структуру с любых сайтов: скрейпинг, обход, поиск.",
            "firecrawl-mcp@3.35.0", "Поиск и веб",
            env: new() { ["FIRECRAWL_API_KEY"] = "${secret:FIRECRAWL_API_KEY}" },
            inputs: [new("FIRECRAWL_API_KEY", "API-ключ Firecrawl", "firecrawl.dev → Dashboard → API Keys.")],
            note: ForeignNote),

        Remote("huggingface", "Hugging Face",
            "Поиск моделей, датасетов и статей на Hugging Face, документация библиотек.",
            "https://huggingface.co/mcp", "Поиск и веб", note: ForeignNote),

        // ── Бизнес и платежи ─────────────────────────────────────────────────
        Npx("stripe", "Stripe",
            "Платежи, клиенты, счета и подписки Stripe. Используйте ограниченный ключ (rk_…) с нужными правами.",
            "@stripe/mcp@0.3.3", "Бизнес и платежи",
            args: ["--tools=all", "--api-key=${secret:STRIPE_SECRET_KEY}"],
            inputs: [new("STRIPE_SECRET_KEY", "Restricted key Stripe (rk_…)", "dashboard.stripe.com → Developers → API keys → Create restricted key (дайте только чтение, если не нужны платежи).")],
            note: "Агент может двигать деньги — выдавайте ключу минимум прав. " + ForeignNote),

        Remote("paypal", "PayPal",
            "Заказы, счета и платежи PayPal.",
            "https://mcp.paypal.com/mcp", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Npx("hubspot", "HubSpot",
            "CRM HubSpot: контакты, компании, сделки, тикеты.",
            "@hubspot/mcp-server@0.4.0", "Бизнес и платежи",
            env: new() { ["PRIVATE_APP_ACCESS_TOKEN"] = "${secret:PRIVATE_APP_ACCESS_TOKEN}" },
            inputs: [new("PRIVATE_APP_ACCESS_TOKEN", "Private app access token", "HubSpot → Settings → Integrations → Private Apps → создать приложение с нужными scope.")],
            note: ForeignNote),

        Remote("intercom", "Intercom",
            "Диалоги и контакты Intercom.",
            "https://mcp.intercom.com/mcp", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Remote("amplitude", "Amplitude",
            "Продуктовая аналитика Amplitude: события, воронки, когорты.",
            "https://mcp.amplitude.com/mcp", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Remote("mixpanel", "Mixpanel",
            "Продуктовая аналитика Mixpanel: отчёты и события.",
            "https://mcp.mixpanel.com/mcp", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Remote("webflow", "Webflow",
            "Сайты и CMS Webflow.",
            "https://mcp.webflow.com/mcp", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Npx("resend", "Resend (почта)",
            "Отправка писем через Resend: транзакционные письма и рассылки от вашего домена.",
            "resend-mcp@2.27.2", "Бизнес и платежи",
            env: new() { ["RESEND_API_KEY"] = "${secret:RESEND_API_KEY}" },
            inputs: [new("RESEND_API_KEY", "API-ключ Resend", "resend.com/api-keys → Create API Key.")],
            note: ForeignNote),

        // ── РФ ───────────────────────────────────────────────────────────────
        // Яндекс Диск, Telegram-бот, ВКонтакте, Битрикс24, Трекер, Метрика, DaData, МойСклад, amoCRM, ЮKassa и курсы ЦБ
        // подключаются без Node.js командой /connect — это встроенные коннекторы (см. ServiceCatalog).
        Remote("dadata-mcp", "DaData (официальный MCP)",
            "Официальный MCP DaData: проверка контрагентов по ИНН (ЕГРЮЛ, финансы, лицензии), адреса и ФИАС, геокодирование. Ключи — на dadata.ru.",
            "https://mcp.dadata.ru/mcp", "РФ",
            inputs:
            [
                new("DADATA_API_KEY", "API-ключ DaData", "dadata.ru → Профиль → API-ключ."),
                new("DADATA_SECRET_KEY", "Секретный ключ DaData", "Там же, рядом с API-ключом."),
            ],
            extraArgs: Header("Authorization"), env: new() { ["AUTH_HEADER"] = "Bearer ${secret:DADATA_API_KEY}:${secret:DADATA_SECRET_KEY}" },
            note: "Данные о компаниях по полному тарифу платные; на бесплатном — базовые сведения. Простые запросы по ИНН и адресам доступны и без Node.js: /connect dadata."),

        Npx("yandex-direct", "Яндекс Директ, Метрика и Вордстат",
            "Рекламные кампании Директа, статистика Метрики и частоты Вордстата: анализ и управление рекламой.",
            "@stegyan/yandex-mcp@1.2.0", "РФ",
            env: new() { ["YANDEX_TOKEN"] = "${secret:YANDEX_TOKEN}" },
            inputs: [new("YANDEX_TOKEN", "OAuth-токен Яндекса", "oauth.yandex.ru → приложение с доступом к Директ API и Метрике → токен через https://oauth.yandex.ru/authorize?response_type=token&client_id=<ID>.")],
            note: CommunityNote, community: true),

        Npx("vk-ads", "VK Реклама",
            "Рекламные кабинеты VK Рекламы: кампании, группы объявлений, баннеры и статистика.",
            "mcp-vk-ads@1.7.0", "РФ",
            env: new() { ["VK_ADS_TOKEN"] = "${secret:VK_ADS_TOKEN}" },
            inputs: [new("VK_ADS_TOKEN", "Токен VK Рекламы", "ads.vk.com → Кабинет → Доступ к API.")],
            note: CommunityNote, community: true),

        Npx("wildberries", "Wildberries (продавец)",
            "Seller API Wildberries: товары, цены, остатки, заказы, поставки, аналитика, отзывы — по токену продавца.",
            "@theyahia/wildberries-mcp@3.1.0", "РФ",
            env: new() { ["WB_API_TOKEN"] = "${secret:WB_API_TOKEN}" },
            inputs: [new("WB_API_TOKEN", "API-токен продавца", "seller.wildberries.ru → Настройки → Доступ к API → создать токен (выдайте только нужные категории).")],
            note: CommunityNote, community: true),

        Npx("cdek", "СДЭК",
            "Доставка СДЭК: расчёт тарифов, заказы, вызов курьера, отслеживание.",
            "@theyahia/cdek-mcp@2.2.0", "РФ",
            env: new()
            {
                ["CDEK_CLIENT_ID"] = "${secret:CDEK_CLIENT_ID}",
                ["CDEK_CLIENT_SECRET"] = "${secret:CDEK_CLIENT_SECRET}",
            },
            inputs:
            [
                new("CDEK_CLIENT_ID", "Account (client_id)", "Личный кабинет СДЭК → Интеграция → Данные для интеграции.", Secret: false),
                new("CDEK_CLIENT_SECRET", "Secure password (client_secret)", "Там же."),
            ],
            note: CommunityNote, community: true),

        Npx("onec", "1С:Предприятие (OData)",
            "Запросы к базе 1С по OData: справочники, документы, остатки, отчёты. Без прав записи, пока вы сами не включите её в настройках публикации.",
            "1c-odata-mcp@0.12.0", "РФ",
            env: new()
            {
                ["ODATA_BASE_URL"] = "${secret:ODATA_BASE_URL}",
                ["ODATA_USERNAME"] = "${secret:ODATA_USERNAME}",
                ["ODATA_PASSWORD"] = "${secret:ODATA_PASSWORD}",
            },
            inputs:
            [
                new("ODATA_BASE_URL", "Адрес OData", "Вид: https://сервер/база/odata/standard.odata/ — публикацию стандартного интерфейса OData включает администратор 1С.", Secret: false),
                new("ODATA_USERNAME", "Пользователь 1С", "Лучше отдельный пользователь с правом только на чтение.", Secret: false),
                new("ODATA_PASSWORD", "Пароль", "Пароль этого пользователя."),
            ],
            note: CommunityNote, community: true),

        Npx("kontur-elba", "Контур.Эльба",
            "Бухгалтерия в Контур.Эльбе: организации, документы, платежи, контрагенты — через официальный Public API.",
            "elba-kontur-mcp@1.0.0", "РФ",
            env: new() { ["ELBA_API_KEY"] = "${secret:ELBA_API_KEY}" },
            inputs: [new("ELBA_API_KEY", "API-ключ Эльбы", "Эльба → Настройки → вкладка «API» → создать ключ.")],
            note: CommunityNote, community: true),

        Npx("yandex-cloud", "Yandex Cloud (официальный)",
            "Официальный MCP Yandex Cloud: управление ресурсами облака (вычисления, сети, сервисы) и документация.",
            "@yandex-cloud/mcp@1.0.1", "РФ",
            args: ["-s", "toolkit"], startup: 240,
            note: "При первом запуске откроется браузер для входа в Yandex Cloud. Серверы: toolkit, search, functions, docs (меняется в /mcp config)."),

        // ── Добавлено 2026-10-27: ещё удалённые MCP самих сервисов и пакеты (версии проверены в npm) ──
        Remote("canva", "Canva",
            "Дизайны Canva: поиск, создание и экспорт презентаций, постов и картинок.",
            "https://mcp.canva.com/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Remote("miro", "Miro",
            "Доски Miro: чтение и создание диаграмм, схем и стикеров.",
            "https://mcp.miro.com", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Remote("airtable-oauth", "Airtable (вход в браузере)",
            "Тот же Airtable без ручного токена: базы и записи по входу через браузер.",
            "https://mcp.airtable.com/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Remote("todoist", "Todoist",
            "Задачи, проекты и метки Todoist.",
            "https://ai.todoist.net/mcp", "Дизайн и документы", note: OAuthNote + " " + ForeignNote),

        Npx("clickup", "ClickUp",
            "Задачи, списки, комментарии и учёт времени ClickUp.",
            "@taazkareem/clickup-mcp-server@0.14.6", "Дизайн и документы",
            env: new()
            {
                ["CLICKUP_API_KEY"] = "${secret:CLICKUP_API_KEY}",
                ["CLICKUP_TEAM_ID"] = "${secret:CLICKUP_TEAM_ID}",
            },
            inputs:
            [
                new("CLICKUP_API_KEY", "Personal API token ClickUp", "ClickUp → Settings → Apps → API Token."),
                new("CLICKUP_TEAM_ID", "Team ID (Workspace ID)", "Цифры в адресе app.clickup.com/<ID>/…", Secret: false),
            ],
            note: CommunityNote + " " + ForeignNote, community: true),

        Npx("microsoft-365", "Microsoft 365 (почта, календарь, OneDrive, Teams)",
            "Outlook, календарь, OneDrive/SharePoint, Excel, OneNote, To Do через Microsoft Graph под вашим аккаунтом.",
            "@softeria/ms-365-mcp-server@0.175.0", "Мессенджеры",
            startup: 240,
            note: CommunityNote + " Вход — по коду устройства в терминале; для Teams/SharePoint рабочего аккаунта добавьте --org-mode в /mcp config. " + ForeignNote,
            community: true),

        Npx("gitlab", "GitLab",
            "Проекты, merge request’ы, issues, пайплайны, wiki и релизы GitLab (gitlab.com или свой сервер).",
            "@zereight/mcp-gitlab@2.3.12", "Разработка",
            env: new()
            {
                ["GITLAB_PERSONAL_ACCESS_TOKEN"] = "${secret:GITLAB_PERSONAL_ACCESS_TOKEN}",
                ["GITLAB_API_URL"] = "${secret:GITLAB_API_URL}",
            },
            inputs:
            [
                new("GITLAB_PERSONAL_ACCESS_TOKEN", "Personal access token GitLab", "GitLab → Preferences → Access tokens; для чтения достаточно read_api."),
                new("GITLAB_API_URL", "Адрес API", "https://gitlab.com/api/v4 или https://ваш-сервер/api/v4 для self-hosted.", Secret: false),
            ],
            note: CommunityNote, community: true),

        Remote("pagerduty", "PagerDuty",
            "Инциденты, дежурства и сервисы PagerDuty.",
            "https://mcp.pagerduty.com/mcp", "Разработка", note: OAuthNote + " " + ForeignNote),

        Remote("microsoft-learn", "Microsoft Learn",
            "Официальная документация Microsoft: .NET, Azure, Windows, Microsoft 365. Без ключа.",
            "https://learn.microsoft.com/api/mcp", "Разработка", note: ForeignNote),

        Npx("shopify-dev", "Shopify (документация и схемы)",
            "Документация и GraphQL-схемы Shopify для разработки тем и приложений. Без ключа.",
            "@shopify/dev-mcp@1.17.0", "Разработка", note: ForeignNote),

        Remote("prisma", "Prisma",
            "Базы Prisma Postgres, миграции и схема.",
            "https://mcp.prisma.io/mcp", "Данные", note: OAuthNote + " " + ForeignNote),

        Remote("planetscale", "PlanetScale",
            "Базы PlanetScale (MySQL/Postgres): схема, ветки, запросы.",
            "https://mcp.pscale.dev/mcp/planetscale", "Данные", note: OAuthNote + " " + ForeignNote),

        Npx("mongodb", "MongoDB (только чтение)",
            "Коллекции, схемы и запросы к вашей MongoDB (локальной или Atlas). Запись выключена.",
            "mongodb-mcp-server@3.1.0", "Данные",
            args: ["--readOnly"],
            env: new() { ["MDB_MCP_CONNECTION_STRING"] = "${secret:MDB_MCP_CONNECTION_STRING}" },
            inputs: [new("MDB_MCP_CONNECTION_STRING", "Строка подключения (mongodb://… или mongodb+srv://…)", "Лучше отдельный пользователь с ролью read.")],
            note: "Режим --readOnly: агент не изменит данные; убрать можно в /mcp config."),

        Remote("clerk", "Clerk",
            "Аутентификация Clerk: пользователи, организации, SDK-сниппеты.",
            "https://mcp.clerk.com/mcp", "Разработка", note: OAuthNote + " " + ForeignNote),

        Remote("netlify", "Netlify",
            "Сайты и деплои Netlify: сборки, окружения, функции.",
            "https://netlify-mcp.netlify.app/mcp", "Облака и инфраструктура", note: OAuthNote + " " + ForeignNote),

        Remote("sanity", "Sanity",
            "Контент и схемы Sanity CMS.",
            "https://mcp.sanity.io", "Данные", note: OAuthNote + " " + ForeignNote),

        Remote("square", "Square",
            "Платежи, каталог и заказы Square.",
            "https://mcp.squareup.com/sse", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Remote("wix", "Wix",
            "Сайты Wix: страницы, CMS, магазин, бронирования.",
            "https://mcp.wix.com/sse", "Бизнес и платежи", note: OAuthNote + " " + ForeignNote),

        Remote("apify", "Apify",
            "Тысячи готовых скрейперов и автоматизаций (соцсети, карты, маркетплейсы, сайты).",
            "https://mcp.apify.com", "Поиск и веб", note: OAuthNote + " " + ForeignNote),

        Remote("exa", "Exa (поиск)",
            "Нейропоиск в вебе и по коду: страницы, статьи, компании. Без ключа (с ограничением запросов).",
            "https://mcp.exa.ai/mcp", "Поиск и веб", note: ForeignNote),

        Npx("mapbox", "Mapbox",
            "Геокодинг, маршруты, изохроны и поиск мест Mapbox.",
            "@mapbox/mcp-server@0.16.0", "Поиск и веб",
            env: new() { ["MAPBOX_ACCESS_TOKEN"] = "${secret:MAPBOX_ACCESS_TOKEN}" },
            inputs: [new("MAPBOX_ACCESS_TOKEN", "Access token Mapbox", "account.mapbox.com → Access tokens (секретный токен с публичными скоупами).")],
            note: ForeignNote),
    ];

    public static CatalogEntry? Find(string id) =>
        Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Разделы в порядке появления.</summary>
    public static IEnumerable<string> Categories => Entries.Select(e => e.Category).Distinct();

    /// <summary>Поиск по названию, описанию и разделу («почта», «задачи», «рф»).</summary>
    public static IEnumerable<CatalogEntry> Search(string query) =>
        Entries.Where(e => e.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || e.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || e.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || e.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
}

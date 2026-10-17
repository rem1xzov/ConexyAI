namespace ConexyAI.Desktop.Mcp;

/// <summary>Что спросить у пользователя при установке сервера из каталога.</summary>
/// <param name="Name">Имя секрета/параметра, на него ссылаются ${secret:Name} в Args/Env.</param>
/// <param name="Prompt">Вопрос в терминале.</param>
/// <param name="Help">Где это взять.</param>
/// <param name="Secret">Скрывать ввод и хранить зашифрованно.</param>
/// <param name="Optional">Можно пропустить (Enter); тогда <see cref="ArgIfSet"/> не добавляется.</param>
/// <param name="ArgIfSet">Аргумент, который добавляется, только если значение введено.</param>
public sealed record CatalogInput(string Name, string Prompt, string Help, bool Secret = true, bool Optional = false, string? ArgIfSet = null);

public sealed record CatalogEntry(
    string Id,
    string Title,
    string Description,
    string Command,
    string[] Args,
    Dictionary<string, string>? Env = null,
    CatalogInput[]? Inputs = null,
    string? Note = null);

/// <summary>
/// Проверенные MCP-серверы для разработчиков: ставятся одной командой /mcp add &lt;id&gt;. Версии закреплены —
/// сервер получает ключи пользователя и работает на его ПК, поэтому «latest» сюда не подходит.
/// Обновлять версии — вручную после проверки.
/// </summary>
public static class McpCatalog
{
    private const string ForeignNote =
        "Сервис зарубежный: из России может не открываться без VPN — тогда инструменты вернут ошибку сети.";

    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        new("playwright", "Playwright (Microsoft)",
            "Браузер под управлением агента: открыть страницу, кликнуть, ввести текст, прочитать консоль — проверка интерфейса как живым пользователем.",
            "npx", ["-y", "@playwright/mcp@0.0.83", "--browser", OperatingSystem.IsWindows() ? "msedge" : "chrome"],
            Note: "Окно браузера откроется на вашем ПК. Инструментов много (~20) — если упрётесь в лимит, отключите ненужные серверы."),

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
            Note: "Режим только чтения: агент не изменит данные и схему. Убрать --read-only можно в /mcp config."),

        new("postgres", "PostgreSQL (только чтение)",
            "Схема таблиц и SELECT-запросы к вашей базе Postgres (локальной или удалённой).",
            "npx", ["-y", "@modelcontextprotocol/server-postgres@0.6.2", "${secret:DATABASE_URL}"],
            Inputs:
            [
                new("DATABASE_URL", "Строка подключения (postgresql://user:pass@host:5432/db)",
                    "Лучше отдельный пользователь БД только с правами SELECT."),
            ],
            Note: "Официальный сервер Anthropic, сейчас в архиве (не обновляется), зато простой и только на чтение."),

        new("notion", "Notion",
            "Страницы и базы Notion: ТЗ, заметки, задачи — агент читает и обновляет их.",
            "npx", ["-y", "@notionhq/notion-mcp-server@2.5.2"],
            Env: new() { ["NOTION_TOKEN"] = "${secret:NOTION_TOKEN}" },
            Inputs:
            [
                new("NOTION_TOKEN", "Internal Integration Secret (ntn_…)",
                    "notion.so/profile/integrations → New integration → Internal; затем на нужных страницах «…» → Connections → добавить интеграцию."),
            ],
            Note: ForeignNote),

        new("figma", "Figma (Framelink)",
            "Макеты Figma для вёрстки: агент получает структуру, размеры, цвета и тексты фрейма по ссылке.",
            "npx", ["-y", "figma-developer-mcp@0.13.2", "--stdio"],
            Env: new() { ["FIGMA_API_KEY"] = "${secret:FIGMA_API_KEY}" },
            Inputs:
            [
                new("FIGMA_API_KEY", "Personal access token Figma",
                    "Figma → Settings → Security → Personal access tokens → Generate new token (права на чтение файлов)."),
            ],
            Note: ForeignNote),
    ];

    public static CatalogEntry? Find(string id) =>
        Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

using System.Text;
using System.Text.Json;

namespace ConexyAI.Desktop.Mcp;

/// <summary>Один инструмент MCP-сервера в списке для поиска.</summary>
public sealed record GatewayTool(string Server, string Tool, string Description, JsonElement Schema);

/// <summary>
/// Шлюз к большому числу MCP-инструментов. Сервер ConexyAI принимает от десктопа не больше 48 инструментов, а у
/// одного Playwright их ~20: подключив пяток серверов, агент «терял» часть возможностей молча. Когда инструментов
/// больше лимита, вместо списка в промпт идут ДВА мета-инструмента — «найти» (поиск по имени и описанию с готовой
/// схемой аргументов) и «вызвать». Так число доступных агенту инструментов не ограничено лимитом.
/// </summary>
public static class McpGateway
{
    public const string ServerSlug = "mcpgw";
    private const int DefaultResults = 8;
    private const int MaxSchemaChars = 1_500;

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Мета-инструменты с описанием того, какие серверы за ними стоят.</summary>
    public static List<LocalToolSpec> Specs(IReadOnlyCollection<GatewayTool> tools)
    {
        var servers = tools.GroupBy(t => t.Server).Select(g => $"{g.Key} ({g.Count()})");
        var list = string.Join(", ", servers);
        var summary = list.Length > 300 ? list[..300] + "…" : list;
        return
        [
            new($"local__{ServerSlug}__find",
                $"Поиск среди {tools.Count} инструментов подключённых MCP-серверов: {summary}. Опишите, что нужно сделать, — вернёт подходящие инструменты со схемой аргументов. Затем вызовите нужный через local__{ServerSlug}__call.",
                Schema("""{"type":"object","properties":{"query":{"type":"string","description":"Ключевые слова по именам инструментов — лучше английские: create issue, send message, list files, read sheet"},"server":{"type":"string","description":"Только этот сервер (необязательно)"},"limit":{"type":"integer","description":"Сколько результатов, по умолчанию 8"}}}""")),
            new($"local__{ServerSlug}__call",
                $"Вызвать инструмент MCP-сервера, найденный через local__{ServerSlug}__find.",
                Schema("""{"type":"object","properties":{"server":{"type":"string","description":"Имя сервера из результата поиска"},"tool":{"type":"string","description":"Имя инструмента"},"arguments":{"type":"object","description":"Аргументы по схеме из результата поиска"}},"required":["server","tool"]}""")),
        ];
    }

    /// <summary>Подбор инструментов под запрос: совпадения слов в имени весомее, чем в описании.</summary>
    public static string Find(IReadOnlyCollection<GatewayTool> tools, string? query, string? server, int? limit)
    {
        var pool = tools.Where(t => string.IsNullOrWhiteSpace(server) || t.Server.Equals(server.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0)
            return string.IsNullOrWhiteSpace(server)
                ? "MCP-инструментов нет."
                : $"Нет сервера «{server}». Есть: {string.Join(", ", tools.Select(t => t.Server).Distinct())}.";

        var take = Math.Clamp(limit ?? DefaultResults, 1, 20);
        var words = Words(query);
        var ranked = words.Count == 0
            ? pool
            : pool.Select(t => (Tool: t, Score: Score(t, words))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Tool).ToList();

        var sb = new StringBuilder();
        if (words.Count == 0)
        {
            sb.AppendLine("Серверы и инструменты (уточните запрос, чтобы получить схему аргументов):");
            foreach (var g in pool.GroupBy(t => t.Server))
                sb.AppendLine($"- {g.Key} ({g.Count()}): {string.Join(", ", g.Take(12).Select(t => t.Tool))}{(g.Count() > 12 ? ", …" : string.Empty)}");
            return sb.ToString().TrimEnd();
        }
        if (ranked.Count == 0)
            return $"По запросу «{query}» ничего не нашлось. Попробуйте другие слова или вызовите find без query — покажет все серверы.";

        sb.AppendLine($"Найдено {ranked.Count}, показаны первые {Math.Min(take, ranked.Count)}. Вызов: local__{ServerSlug}__call {{server, tool, arguments}}.");
        foreach (var t in ranked.Take(take))
        {
            var description = t.Description.Replace('\n', ' ').Trim();
            sb.AppendLine($"\n• {t.Server} / {t.Tool} — {(description.Length > 240 ? description[..240] + "…" : description)}");
            var schema = t.Schema.GetRawText();
            sb.AppendLine("  аргументы: " + (schema.Length > MaxSchemaChars ? schema[..MaxSchemaChars] + "…" : schema));
        }
        return sb.ToString().TrimEnd();
    }

    private static List<string> Words(string? query) =>
        (query ?? string.Empty).ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '/', '-', '_', '«', '»', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Distinct()
            .ToList();

    private static int Score(GatewayTool tool, List<string> words)
    {
        var name = tool.Tool.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        var server = tool.Server.ToLowerInvariant();
        var description = tool.Description.ToLowerInvariant();
        var score = 0;
        foreach (var w in words)
        {
            if (name.Contains(w, StringComparison.Ordinal)) score += 5;
            if (server.Contains(w, StringComparison.Ordinal)) score += 3;
            if (description.Contains(w, StringComparison.Ordinal)) score += 1;
        }
        return score;
    }
}

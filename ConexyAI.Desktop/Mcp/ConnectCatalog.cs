namespace ConexyAI.Desktop.Mcp;

public enum ConnectKind { Google, Mail, Service, Mcp }

/// <summary>Одна строка единого каталога /connect: что подключается и подключено ли уже.</summary>
/// <param name="NeedsNode">Нужен Node.js (команда npx).</param>
/// <param name="Vpn">Сервис зарубежный: из России может не открываться без VPN.</param>
public sealed record ConnectItem(
    string Id,
    string Title,
    string Description,
    string Category,
    ConnectKind Kind,
    bool Connected,
    bool NeedsNode = false,
    bool Vpn = false);

/// <summary>
/// Единый каталог подключений для /connect: встроенные Google и почта, REST-сервисы РФ (без Node.js) и MCP-серверы
/// из каталога. Пользователю не нужно знать, какой механизм стоит за сервисом, — только название.
/// </summary>
public static class ConnectCatalog
{
    public const string MailCategory = "Почта и календарь";

    private static readonly string[] CategoryOrder =
    [
        "РФ", "Мессенджеры", MailCategory, "Дизайн и документы", "Разработка", "Облака и инфраструктура",
        "Данные", "Бизнес и платежи", "Поиск и веб", "Служебные",
    ];

    public static IReadOnlyList<ConnectItem> Build(
        bool googleConnected, bool mailConnected, Func<ServiceDef, bool> serviceConnected, McpConfig config)
    {
        var items = new List<ConnectItem>
        {
            new("google", "Google (Gmail, Календарь, Диск)",
                "Читать и искать письма, события календаря и файлы Диска; отправка — с подтверждения.",
                MailCategory, ConnectKind.Google, googleConnected),
            new("mail", "Почта: Яндекс, Mail.ru, Рамблер, хостинги",
                "Поиск и чтение писем, вложения, отправка с подтверждения — по паролю приложения (IMAP/SMTP).",
                MailCategory, ConnectKind.Mail, mailConnected),
        };

        items.AddRange(ServiceCatalog.Services.Select(s =>
            new ConnectItem(s.Id, s.Title, s.Description, s.Category, ConnectKind.Service, serviceConnected(s))));

        foreach (var e in McpCatalog.Entries)
        {
            var connected = config.Servers.ContainsKey(e.Id) || config.Servers.Values.Any(s => s.CatalogId == e.Id);
            items.Add(new ConnectItem(e.Id, e.Title, e.Description, e.Category, ConnectKind.Mcp, connected,
                NeedsNode: e.Command == "npx", Vpn: e.Note?.Contains("из России", StringComparison.Ordinal) == true));
        }

        return items;
    }

    /// <summary>Разделы в привычном порядке: сначала РФ и мессенджеры, прочие — в конце.</summary>
    public static IEnumerable<string> Categories(IEnumerable<ConnectItem> items)
    {
        var present = items.Select(i => i.Category).Distinct().ToList();
        return present.OrderBy(c => { var i = Array.IndexOf(CategoryOrder, c); return i < 0 ? int.MaxValue : i; })
            .ThenBy(c => c, StringComparer.Ordinal);
    }

    private static string Normalize(string id) => id.Trim().Replace('-', '_').ToLowerInvariant();

    public static ConnectItem? Find(IEnumerable<ConnectItem> items, string id)
    {
        var wanted = Normalize(id);
        return items.FirstOrDefault(i => Normalize(i.Id) == wanted);
    }

    /// <summary>Поиск по всем словам запроса в названии, описании, id и разделе.</summary>
    public static IEnumerable<ConnectItem> Search(IEnumerable<ConnectItem> items, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return [];
        return items.Where(i => words.All(w =>
            i.Id.Contains(w, StringComparison.OrdinalIgnoreCase)
            || i.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
            || i.Description.Contains(w, StringComparison.OrdinalIgnoreCase)
            || i.Category.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }
}

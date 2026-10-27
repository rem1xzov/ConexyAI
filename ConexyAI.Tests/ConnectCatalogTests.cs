using System.Runtime.CompilerServices;
using ConexyAI.Desktop.Mcp;

// CONNECT_HUB: единый каталог /connect собирает Google, почту, REST-сервисы РФ и MCP в один список.
internal static class ConnectCatalogTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("connect hub: catalog has unique ids and covers every service and MCP entry", CoversAllAsync);
        TestRegistry.Add("connect hub: find and search work by id, title and topic", SearchAsync);
        TestRegistry.Add("connect hub: service and mcp ids are unique across both catalogs", UniqueAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static IReadOnlyList<ConnectItem> Items(bool google = false) =>
        ConnectCatalog.Build(google, false, _ => false, new McpConfig());

    private static Task CoversAllAsync()
    {
        var items = Items();
        Assert(items.Count == 2 + ServiceCatalog.Services.Count + McpCatalog.Entries.Count, "every catalog entry is listed");
        Assert(items.Count(i => i.Kind == ConnectKind.Service) == ServiceCatalog.Services.Count, "services listed");
        Assert(Items(google: true).First(i => i.Id == "google").Connected, "google status comes from the caller");
        return Task.CompletedTask;
    }

    private static Task SearchAsync()
    {
        var items = Items();
        Assert(ConnectCatalog.Find(items, "yandex-disk")?.Id == "yandex_disk", "hyphen and underscore ids are equivalent");
        Assert(ConnectCatalog.Find(items, "FIGMA")?.Kind == ConnectKind.Mcp, "find is case-insensitive");
        Assert(ConnectCatalog.Find(items, "nope") is null, "unknown id gives nothing");
        Assert(ConnectCatalog.Search(items, "озон").Any(i => i.Id == "ozon_seller"), "search finds Ozon by its Russian name");
        Assert(ConnectCatalog.Search(items, "почта").Any(i => i.Id == "mail"), "search by topic finds mail");
        Assert(!ConnectCatalog.Search(items, "   ").Any(), "blank query finds nothing");
        Assert(ConnectCatalog.Categories(items).First() == "РФ", "Russian services come first");
        return Task.CompletedTask;
    }

    private static Task UniqueAsync()
    {
        var items = Items();
        var normalized = items.Select(i => i.Id.Replace('-', '_').ToLowerInvariant()).ToList();
        var duplicate = normalized.GroupBy(x => x).FirstOrDefault(g => g.Count() > 1)?.Key;
        Assert(duplicate is null, "duplicate id: " + duplicate);
        return Task.CompletedTask;
    }
}

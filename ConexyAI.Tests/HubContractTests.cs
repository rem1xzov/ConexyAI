using System.Reflection;
using System.Runtime.CompilerServices;
using ConexyAI.Hub;

// SIGNALR_NO_OVERLOADS: SignalR строит диспетчер хаба прямо в MapHub, и перегрузка метода с тем же
// именем роняет старт приложения (проверено: DefaultHubDispatcher..ctor бросает исключение).
// В проде это выглядело как «контейнер backend unhealthy» после деплоя — поэтому проверяем тестом.
internal static class HubContractTests
{
    [ModuleInitializer]
    internal static void Register() =>
        TestRegistry.Add("hub: method names are unique (SignalR rejects overloads at startup)", UniqueNamesAsync);

    private static Task UniqueNamesAsync()
    {
        var names = typeof(ConexyHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        var duplicates = names
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        TestRegistry.Assert(
            duplicates.Count == 0,
            $"overloaded hub methods break MapHub at startup: {string.Join(", ", duplicates)}");
        return Task.CompletedTask;
    }
}

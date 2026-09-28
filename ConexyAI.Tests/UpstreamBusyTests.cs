using System.Net;
using System.Runtime.CompilerServices;
using ConexyAI.Service;

// UPSTREAM_OVERLOAD: добавлено 2026-09-28
/// <summary>
/// Сообщение о перегрузке провайдера: пользователь должен увидеть причину и время ожидания, но не
/// тело ответа провайдера (там случаются служебные детали и обрывки запроса).
/// </summary>
internal static class UpstreamBusyTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("upstream: a rate limit becomes a readable overload message", RateLimitAsync);
        TestRegistry.Add("upstream: an outage reads as temporarily unavailable", OutageAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task RateLimitAsync()
    {
        var text = ConexyLlmClient.BusyMessage(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(45));

        Assert(text.Contains("высокая нагрузка"), $"the reason must be stated, got: {text}");
        Assert(text.Contains("45 сек."), $"the provider's suggestion must be used, got: {text}");
        Assert(!text.Contains("429") && !text.Contains("{"), $"no technical details may leak, got: {text}");

        // Без подсказки провайдера ждём минуту — и это тоже должно быть видно пользователю.
        var fallback = ConexyLlmClient.BusyMessage(HttpStatusCode.TooManyRequests, null);
        Assert(fallback.Contains("1 мин."), $"a minute is the default wait, got: {fallback}");

        return Task.CompletedTask;
    }

    private static Task OutageAsync()
    {
        var text = ConexyLlmClient.BusyMessage(HttpStatusCode.ServiceUnavailable, null);
        Assert(text.Contains("временно недоступен"), $"a 5xx must not be called a rate limit, got: {text}");
        return Task.CompletedTask;
    }
}

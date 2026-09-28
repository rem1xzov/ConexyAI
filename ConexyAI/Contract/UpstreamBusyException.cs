namespace ConexyAI.Contract;

// UPSTREAM_OVERLOAD: добавлено 2026-09-28
/// <summary>
/// Провайдер ИИ перегружен: 429 (лимит запросов) или 5xx, и все повторы уже исчерпаны.
/// <para>
/// Сообщение адресовано ПОЛЬЗОВАТЕЛЮ и намеренно не содержит тела ответа провайдера (там бывают и
/// служебные детали, и обрывки запроса). Подробности остаются в логах бэкенда, а в чат уходит
/// понятная фраза с примерным временем ожидания.
/// </para>
/// </summary>
public class UpstreamBusyException : Exception
{
    public UpstreamBusyException(string message) : base(message) { }
}

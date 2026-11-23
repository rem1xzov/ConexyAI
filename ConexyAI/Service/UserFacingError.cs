using System.Net;
using System.Text.RegularExpressions;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// USER_FACING_ERRORS: коммерческий сервис не должен показывать клиенту сырые ответы провайдера,
// request-id, внутренние URL/токены и куски JSON. Внутренние детали остаются в логах; наружу —
// короткая безопасная фраза. Это единственная точка для сообщений об ошибках, которые видит юзер.
public static class UserFacingError
{
    public const string Generic = "Не удалось выполнить запрос. Попробуйте ещё раз.";

    public static string From(Exception ex, string fallback = Generic) => ex switch
    {
        SafeUserFacingException safe => safe.Message,
        UpstreamBusyException busy => busy.Message,
        OperationCanceledException => "Запрос отменён.",
        TimeoutException => "Операция заняла слишком много времени. Попробуйте ещё раз.",
        HttpRequestException => "Сервис модели временно недоступен или отклонил запрос. Попробуйте ещё раз.",
        UnauthorizedAccessException => "Нет доступа к запрошенному файлу или ресурсу.",
        IOException => "Не удалось прочитать или записать файл. Проверьте, что он не занят.",
        _ => fallback,
    };

    /// <summary>Ошибка инструмента для модели: без сырых тел ответа и секретов.</summary>
    public static string Tool(Exception ex, string tool)
    {
        var detail = Scrub(ex.Message, 260);
        return detail.Length == 0 ? $"{tool}: не удалось выполнить действие." : $"{tool}: {detail}";
    }

    /// <summary>Ответ upstream-провайдера без его тела и request-id.</summary>
    public static string Upstream(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            "Не удалось обработать запрос модели. Проверьте вложение (изображение или документ) и повторите.",
        HttpStatusCode.RequestEntityTooLarge =>
            "Вложение слишком большое для модели. Уменьшите файл и повторите.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "Сервис модели отклонил запрос. Попробуйте позже.",
        HttpStatusCode.TooManyRequests =>
            "Модель сейчас перегружена. Подождите немного и повторите.",
        _ => "Сервис модели временно недоступен. Попробуйте ещё раз.",
    };

    private static readonly Regex SecretPatterns = new(
        @"sk-[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|" +
        @"(?i)(authorization|bearer|api[_-]?key|access[_-]?token|refresh[_-]?token|secret|password)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RequestId = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b|" +
        @"\b[0-9a-fA-F]{24,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Url = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowsPath = new(@"[A-Za-z]:\\[^\s""'<>]+", RegexOptions.Compiled);
    private static readonly Regex UnixPath = new(@"(?<!\w)/(?:srv|home|root|opt|var|tmp)/[^\s""'<>]+", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);

    /// <summary>Маскирует секреты, ID, URL и пути; сырой JSON/HTML отбрасывает целиком.</summary>
    public static string Scrub(string? value, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        if (text.StartsWith('{') || text.StartsWith('[')
            || text.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid_request_error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<html", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        text = SecretPatterns.Replace(text, "***");
        text = RequestId.Replace(text, "<id>");
        text = Url.Replace(text, "<url>");
        text = WindowsPath.Replace(text, "<path>");
        text = UnixPath.Replace(text, "<path>");
        text = Email.Replace(text, "<email>");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}

/// <summary>Исключение, текст которого уже безопасен для показа пользователю.</summary>
public sealed class SafeUserFacingException : Exception
{
    public SafeUserFacingException(string message) : base(message) { }
    public SafeUserFacingException(string message, Exception inner) : base(message, inner) { }
}

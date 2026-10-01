using System.Runtime.CompilerServices;
using ConexyAI.Service;

// CHAT_TITLE_TOPIC: добавлено 2026-10-01
/// <summary>
/// Название чата выводится из темы разговора отдельной моделью, поэтому ответ надо привести к
/// годному виду. Логика чистая (только строки) — проверяется без воркера и БД.
/// </summary>
internal static class ChatTitleTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("chat title: model output is normalized into a clean title", SanitizeAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static Task SanitizeAsync()
    {
        Assert(ChatTitleWorker.Sanitize("Игра Тетрис") == "Игра Тетрис", "a plain title passes through");
        Assert(ChatTitleWorker.Sanitize("«Игра Тетрис»") == "Игра Тетрис", "guillemets are stripped");
        Assert(ChatTitleWorker.Sanitize("\"Настройка Nginx\"") == "Настройка Nginx", "quotes are stripped");
        Assert(ChatTitleWorker.Sanitize("Название: Отчёт по продажам") == "Отчёт по продажам", "a 'Название:' prefix is dropped");
        Assert(ChatTitleWorker.Sanitize("Игра Тетрис.") == "Игра Тетрис", "a trailing period is removed");
        Assert(ChatTitleWorker.Sanitize("Игра   Тетрис\nвторая строка") == "Игра Тетрис", "extra whitespace collapses and only the first line is used");
        Assert(ChatTitleWorker.Sanitize("   ") is null, "whitespace-only gives no title");
        Assert(ChatTitleWorker.Sanitize(null) is null, "null gives no title");

        // Слишком длинное название обрезается по границе слова и укладывается в лимит.
        var longTitle = string.Join(' ', Enumerable.Repeat("слово", 40));
        var trimmed = ChatTitleWorker.Sanitize(longTitle);
        Assert(trimmed is not null && trimmed.Length <= 80, $"a long title must be capped, got {trimmed?.Length}");
        Assert(trimmed is not null && trimmed.Split(' ').All(word => word == "слово"),
            "the cap must fall on a word boundary, never mid-word");
        return Task.CompletedTask;
    }
}

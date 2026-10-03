using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// TOKEN_ECONOMY: добавлено 2026-10-01.
//
// Дорогие фоновые блоки (персонализация, долговременная память, другие чаты) дописывались в системный
// промпт КАЖДОГО запроса и пересылались на каждой итерации агента — даже когда пользователь просто
// написал «привет». Здесь дешёвая, предсказуемая эвристика: короткая реплика без просьбы вспомнить
// или учесть личное идёт БЕЗ этих блоков, а любая содержательная просьба — с ними.
//
// Логика намеренно консервативная: сомнительный случай трактуется как «контекст нужен» (то есть
// блоки остаются), потому что лишние токены дешевле, чем внезапно забытая память о пользователе.
public static class PromptEconomy
{
    /// <summary>
    /// Всё, что длиннее этого, считается содержательной просьбой, а не репликой: тогда блоки
    /// контекста подключаются всегда.
    /// </summary>
    public const int TrivialMaxChars = 160;

    // Просьбы вспомнить или учесть личный контекст — блоки обязательны, даже если реплика короткая.
    private static readonly string[] PersonalContextMarkers =
    {
        "меня зовут", "моё имя", "мое имя", "обо мне", "мой проект", "мой стек", "моя команда",
        "мы обсуждали", "мы говорили", "как раньше", "как обычно", "как договаривались",
        "помнишь", "в прошлом чате", "в другом чате", "в прошлый раз", "раньше говорил",
        "мои предпочтения", "я предпочитаю", "напомни", "как меня",
    };

    /// <summary>
    /// Нужны ли этому запросу дорогие блоки контекста (персонализация, память, другие чаты).
    /// Пустая реплика — не нужны; короткая реплика без маркеров — не нужны; всё остальное — нужны.
    /// </summary>
    public static bool NeedsAuxiliaryContext(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.Trim();
        if (text.Length > TrivialMaxChars)
            return true;

        var lower = text.ToLowerInvariant();
        foreach (var marker in PersonalContextMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // Краткие реплики, на которые агенту не нужны ни устав, ни инструменты. Список намеренно
    // СОСТОИТ ИЗ ТОЧНЫХ ФРАЗ после нормализации: незнакомая формулировка не распознаётся, и агент
    // работает как обычно — так ошибка в сторону «полного режима» не ломает настоящую задачу.
    private static readonly HashSet<string> SmallTalkPhrases = new(StringComparer.Ordinal)
    {
        "привет", "приветик", "здравствуй", "здравствуйте", "добрый день", "добрый вечер",
        "доброе утро", "доброй ночи", "хай", "hi", "hello", "hey", "как дела", "как ты",
        "как жизнь", "что нового", "спасибо", "благодарю", "спасибо большое", "ок", "окей",
        "хорошо", "понятно", "понял", "ясно", "кто ты", "ты кто", "что ты умеешь", "что умеешь",
        "чем ты можешь помочь", "чем можешь помочь", "как тебя зовут", "ты бот", "ты человек",
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Приветствие или светская реплика: тогда агенту не нужны ни полный устав, ни схемы инструментов.
    /// Строгая проверка: короткая строка, которая ПОСЛЕ нормализации совпадает с известной фразой.
    /// </summary>
    public static bool IsSmallTalk(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.Trim();
        if (text.Length > 60)
            return false;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == ' ')
                sb.Append(ch);
        }

        return SmallTalkPhrases.Contains(Whitespace.Replace(sb.ToString(), " ").Trim());
    }

    /// <summary>Короткий системный промпт для приветствия вместо полного устава агента.</summary>
    public const string SmallTalkAgentPrompt =
        """
        Ты — ConexyAI. Пользователь поздоровался или задал короткий вопрос о тебе — ответь коротко, дружелюбно и по делу, на языке пользователя. Не вызывай инструменты, не создавай план и не рассуждай пространно.
        """;
}

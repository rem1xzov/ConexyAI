using ConexyAI.Contract;

namespace ConexyAI.Service;

// TOKEN_ECONOMY: добавлено 2026-10-06
/// <summary>
/// Сжатие контекста внутри ОДНОГО агентского прогона.
/// <para>
/// История растёт монотонно (каждый шаг пересылает всё заново), а выводы инструментов — чтение
/// файлов, вывод <c>bash</c>, страницы — бывают очень длинными. Метод оставляет последние
/// <see cref="KeepFullOutputs"/> выводов нетронутыми (по ним агент как раз и работает), а более
/// старые и длинные обрезает до головы с пометкой. Ранний вывод при необходимости можно получить
/// заново вызовом инструмента.
/// </para>
/// <para>
/// Живёт отдельно от раннера, чтобы логика была чистой и покрытой тестами (как
/// <see cref="AgentLoopGuard"/>). Позиция и роль сообщений не меняются — рушится только их содержимое,
/// поэтому протокол tool-ответов (tool сразу за своим assistant) остаётся валидным.
/// </para>
/// </summary>
public static class AgentContextCompressor
{
    /// <summary>Сколько последних выводов инструментов остаются неприкосновенными.</summary>
    public const int KeepFullOutputs = 8;

    /// <summary>С какого размера (символов) старый вывод инструмента подлежит сжатию.</summary>
    public const int TrimThresholdChars = 4_000;

    /// <summary>Сколько символов головы старого вывода оставляем при сжатии.</summary>
    public const int TrimHeadChars = 1_500;

    /// <summary>
    /// PREFIX_CACHE: сжимаем ПАЧКАМИ по столько выводов, а не по одному на каждом шаге. Любая правка
    /// старого сообщения обрывает кэш префикса на нём: всё, что после, оплачивается по полной цене.
    /// По одному выводу за шаг это случалось на каждом шаге длинного прогона (и экономия на сжатии
    /// съедалась во много раз переплатой за кэш), а пачками — раз в TrimBatch вызовов инструментов.
    /// </summary>
    public const int TrimBatch = 8;

    /// <summary>Мутирует список сообщений на месте: обрезает только старые длинные tool-выводы.</summary>
    public static void Compress(List<ChatMessage> messages)
    {
        var toolIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "tool") toolIndexes.Add(i);
        }

        var overflow = toolIndexes.Count - KeepFullOutputs;
        var trimCount = overflow <= 0 ? 0 : overflow / TrimBatch * TrimBatch;
        for (var k = 0; k < trimCount; k++)
        {
            var index = toolIndexes[k];
            if (messages[index].Content is not string text || text.Length <= TrimThresholdChars)
                continue;

            var trimmed = text[..TrimHeadChars] +
                $"\n\n[... вывод обрезан для экономии контекста: показаны первые {TrimHeadChars} из {text.Length} символов. При необходимости перечитай данные инструментом ...]";
            messages[index] = messages[index] with { Content = trimmed };
        }
    }
}

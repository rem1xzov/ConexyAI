using ConexyAI.Contract;

namespace ConexyAI.Service;

// TOKEN_ECONOMY: добавлено 2026-10-06
// CONTEXT_PRESSURE: переработано 2026-11-20 (идея DeepSeek Harness, compaction-basic) — сжатие
// включается по ИЗМЕРЕННОМУ давлению контекста, а не по числу сообщений: пока в окне есть запас,
// старые выводы не трогаются (любая их правка обрывает кэш префикса). Когда давление выше порога —
// обрезаем все старые длинные выводы, оставляя голову И ХВОСТ: ошибки сборки/тестов почти всегда в
// конце вывода, и прежняя обрезка «только первая 1 200 символов» теряла именно их.
/// <summary>
/// Сжатие контекста внутри ОДНОГО агентского прогона.
/// <para>
/// Свежие <see cref="KeepFullOutputs"/> выводов инструментов не трогаются (по ним агент работает),
/// старые и длинные обрезаются до головы+хвоста с пометкой. Ранний вывод можно получить заново
/// вызовом инструмента.
/// </para>
/// <para>
/// Живёт отдельно от раннера, чтобы логика была чистой и покрытой тестами (как
/// <see cref="AgentLoopGuard"/>). Позиция и роль сообщений не меняются — меняется только содержимое,
/// поэтому протокол tool-ответов (tool сразу за своим assistant) остаётся валидным.
/// </para>
/// </summary>
public static class AgentContextCompressor
{
    /// <summary>Сколько последних выводов инструментов остаются неприкосновенными.</summary>
    // TOKEN_ECONOMY: каждый неприкосновенный вывод пересылается на каждом шаге. Четырёх хватает.
    public const int KeepFullOutputs = 4;

    /// <summary>С какого размера (символов) старый вывод инструмента подлежит сжатию.</summary>
    public const int TrimThresholdChars = 2_500;

    /// <summary>Сколько символов головы старого вывода оставляем при сжатии.</summary>
    public const int TrimHeadChars = 1_000;

    /// <summary>Сколько символов хвоста оставляем: итог сборки, ошибки тестов и exit-код живут в конце.</summary>
    public const int TrimTailChars = 600;

    /// <summary>
    /// PREFIX_CACHE: сжимаем ПАЧКАМИ по столько выводов, а не по одному на каждом шаге. Любая правка
    /// старого сообщения обрывает кэш префикса на нём: всё, что после, оплачивается по полной цене.
    /// По одному выводу за шаг это случалось на каждом шаге длинного прогона (и экономия на сжатии
    /// съедалась во много раз переплатой за кэш), а пачками — раз в TrimBatch вызовов инструментов.
    /// Под давлением (см. <see cref="Compress(List{ChatMessage}, int, int, double)"/>) батчи не нужны:
    /// запас окна важнее кэша.
    /// </summary>
    public const int TrimBatch = 8;

    /// <summary>Доля окна, после которой контекст считается «под давлением».</summary>
    public const double DefaultPressureRatio = 0.8;

    /// <summary>Мутирует список сообщений на месте: обрезает только старые длинные tool-выводы (легаси-режим без окна).</summary>
    public static void Compress(List<ChatMessage> messages) => Compress(messages, 0, 0);

    /// <summary>
    /// Сжимает старые длинные выводы. Когда задано окно контекста, сжатие включается только при
    /// давлении выше <paramref name="pressureRatio"/>; под давлением обрезаются ВСЕ старые выводы
    /// (без ожидания полной пачки), иначе — пачками по <see cref="TrimBatch"/>.
    /// </summary>
    /// <param name="messages">История прогона; меняется на месте.</param>
    /// <param name="contextWindowTokens">Окно модели в токенах; &lt;= 0 — легаси-режим по числу сообщений.</param>
    /// <param name="reservedTokens">Сколько токенов окна оставить под ответ модели и следующий шаг.</param>
    /// <param name="pressureRatio">Доля доступного окна, после которой включается сжатие (0.5–0.95).</param>
    public static ContextCompressionResult Compress(
        List<ChatMessage> messages,
        int contextWindowTokens,
        int reservedTokens,
        double pressureRatio = DefaultPressureRatio)
    {
        var tokensBefore = TokenMeter.Estimate(messages);

        var budget = 0;
        if (contextWindowTokens > 0)
        {
            var reserved = Math.Max(0, reservedTokens);
            budget = Math.Max(1_000, contextWindowTokens - reserved);
            var threshold = (int)(budget * Math.Clamp(pressureRatio, 0.5, 0.95));
            if (tokensBefore <= threshold)
                return new ContextCompressionResult(false, tokensBefore, tokensBefore, 0);
        }

        var toolIndexes = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "tool") toolIndexes.Add(i);
        }

        var overflow = toolIndexes.Count - KeepFullOutputs;
        if (overflow <= 0)
            return new ContextCompressionResult(false, tokensBefore, tokensBefore, 0);

        // Под давлением окна режем все старые выводы: цель — уложиться в бюджет, а не сэкономить на кэше.
        // Без окна сохраняем прежнюю батчевую семантику (кэш префикса стабилен между шагами).
        var trimCount = budget > 0 ? overflow : overflow / TrimBatch * TrimBatch;
        var trimmed = 0;
        for (var k = 0; k < trimCount; k++)
        {
            var index = toolIndexes[k];
            if (messages[index].Content is not string text || text.Length <= TrimThresholdChars)
                continue;

            var head = text[..TrimHeadChars];
            var tail = text[^TrimTailChars..];
            messages[index] = messages[index] with
            {
                Content = head +
                          $"\n\n[... вывод обрезан для экономии контекста: показаны первые {TrimHeadChars} и последние " +
                          $"{TrimTailChars} из {text.Length} символов. При необходимости перечитай данные инструментом ...]\n\n" +
                          tail,
            };
            trimmed++;
        }

        return new ContextCompressionResult(trimmed > 0, tokensBefore, TokenMeter.Estimate(messages), trimmed);
    }
}

/// <summary>Итог одного прохода сжатия: было/стало токенов и сколько выводов обрезано.</summary>
/// <param name="Applied">Было ли что-то реально обрезано.</param>
/// <param name="EstimatedTokensBefore">Оценка контекста до сжатия.</param>
/// <param name="EstimatedTokensAfter">Оценка контекста после сжатия.</param>
/// <param name="TrimmedOutputs">Сколько tool-выводов обрезано.</param>
public sealed record ContextCompressionResult(
    bool Applied,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter,
    int TrimmedOutputs);

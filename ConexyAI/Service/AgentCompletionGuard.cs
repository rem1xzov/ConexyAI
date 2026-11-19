using System.Text;

namespace ConexyAI.Service;

/// <summary>Вид улики, доказывающей, что работа над ходом не завершена.</summary>
public enum CompletionEvidenceKind
{
    /// <summary>Команда реально выполнилась и завершилась ошибкой, а позже её так и не исправили.</summary>
    FailedCommand,

    /// <summary>Один и тот же вызов подряд вернул идентичный вывод — ход не двигается.</summary>
    RepeatedOutput,
}

/// <summary>Одна улика незавершённой работы, снятая на границе завершения хода.</summary>
/// <param name="Kind">Что именно доказано.</param>
/// <param name="Tool">Инструмент, которым получена улика.</param>
/// <param name="Key">Стабильная идентичность улики: по ней видно, что новая попытка ничего не изменила.</param>
/// <param name="Summary">Одна строка для лога и для модели.</param>
/// <param name="Detail">Хвост реального вывода, ограниченный по длине.</param>
public sealed record CompletionEvidence(
    CompletionEvidenceKind Kind,
    string Tool,
    string Key,
    string Summary,
    string Detail);

/// <summary>
/// COMPLETION_GUARD: надзор за границей завершения хода по ДОКАЗАТЕЛЬСТВАМ, а не по формулировкам модели.
/// <para>
/// Существующие гейты завершения (plan, incomplete-work, verify) читают текст ответа и пропускают ход,
/// когда модель написала «продолжай», но не оставила следов незавершённой работы, либо когда прогон уже
/// переведён в принудительный текстовый ответ. В результате ход закрывался сразу после упавшей команды
/// или после серии одинаковых результатов — пользователь получал отписку вместо результата.
/// </para>
/// <para>
/// Этот страж смотрит только на факты: последняя команда упала и её никто не исправил; один вызов
/// подряд возвращает идентичный вывод. Улик нет — ход завершается как раньше. Логика чистая (без БД,
/// сети и DI), экземпляр создаётся на прогон, как <see cref="AgentLoopGuard"/>.
/// </para>
/// </summary>
public sealed class AgentCompletionGuard
{
    /// <summary>Сколько одинаковых выводов подряд считаются отсутствием прогресса.</summary>
    public const int RepeatThreshold = 3;

    /// <summary>Сколько символов вывода улики показывается модели.</summary>
    public const int MaxEvidenceChars = 600;

    private readonly Dictionary<string, CompletedCall> _pendingFailures = new(StringComparer.Ordinal);
    private readonly List<CompletedCall> _trailing = new();
    private long _sequence;

    /// <summary>
    /// Запомнить реально выполненный вызов: только по нему видно, что повтор ничего не меняет.
    /// Отказы политики сюда не попадают — их учитывает <see cref="AgentLoopGuard"/>.
    /// </summary>
    /// <param name="tool">Имя инструмента.</param>
    /// <param name="arguments">Аргументы вызова в том виде, в каком их вернула модель.</param>
    /// <param name="output">Вывод инструмента.</param>
    /// <param name="isError">Инструмент/команда вернули ошибку.</param>
    /// <param name="commandExecuted">Команда действительно выполнилась (а не была отклонена до запуска).</param>
    public void Record(string tool, string arguments, string output, bool isError, bool commandExecuted)
    {
        var normalized = AgentLoopGuard.NormalizeOutput(output);
        var call = new CompletedCall(
            tool,
            KeyOf(tool, arguments, normalized),
            isError && commandExecuted,
            Excerpt(normalized),
            ++_sequence);


        // FAILED_ACTION: неудача инструмента снимается только его же успешным вызовом. Успех другого
        // инструмента ничего не доказывает — сборка остаётся упавшей, даже если потом прочитали файл.
        if (call.Failed)
        {
            _pendingFailures[tool] = call;
        }
        else
        {
            _pendingFailures.Remove(tool);
        }

        // NO_PROGRESS: держим серию идентичных вызовов; любой другой вывод её обрывает.
        if (_trailing.Count > 0 && string.Equals(_trailing[^1].Key, call.Key, StringComparison.Ordinal))
        {
            _trailing.Add(call);
        }
        else
        {
            _trailing.Clear();
            _trailing.Add(call);
        }
    }

    /// <summary>Все улики, которые видны на текущей границе завершения. Пустой список — завершаться можно.</summary>
    /// <returns>Улики в порядке важности: неисправленный сбой, затем отсутствие прогресса.</returns>
    public IReadOnlyList<CompletionEvidence> Inspect()
    {
        var evidence = new List<CompletionEvidence>(2);

        var failed = PendingFailure();
        if (failed is not null)
        {
            evidence.Add(new CompletionEvidence(
                CompletionEvidenceKind.FailedCommand,
                failed.Tool,
                "failed:" + failed.Key,
                "последняя команда завершилась ошибкой, а позже её никто не исправил",
                failed.Excerpt));
        }

        var repeated = TrailingRepeat();
        if (repeated is not null)
        {
            evidence.Add(new CompletionEvidence(
                CompletionEvidenceKind.RepeatedOutput,
                repeated.Tool,
                "repeat:" + repeated.Key,
                $"один и тот же вызов подряд вернул идентичный вывод {_trailing.Count} раз(а)",
                repeated.Excerpt));
        }

        return evidence;
    }

    /// <summary>
    /// Текст продолжения для модели: что именно не решено и что требуется перед завершением.
    /// </summary>
    /// <param name="evidence">Улики с <see cref="Inspect"/>; пустыми быть не могут.</param>
    /// <param name="escalated">Та же улика пережила предыдущее принудительное продолжение.</param>
    /// <param name="attempt">Номер продолжения в этом прогоне (с единицы).</param>
    /// <param name="max">Бюджет продолжений в этом прогоне.</param>
    /// <param name="toolsReopened">Ход был переведён в принудительный ответ, и инструменты только что открыты снова.</param>
    /// <returns>Системное сообщение для следующего запроса к модели.</returns>
    public static string BuildPrompt(
        IReadOnlyList<CompletionEvidence> evidence,
        bool escalated,
        int attempt,
        int max,
        bool toolsReopened)
    {
        var text = new StringBuilder();
        text.Append(escalated
            ? "[Completion Guard — Escalated] Та же улика пережила принудительное продолжение "
            : "[Completion Guard] Не заканчивай ход: за прогоном остались неразрешённые доказательства ");
        text.Append('(').Append(attempt).Append('/').Append(max).Append(").\n");
        text.Append(Render(evidence)).Append('\n');

        if (toolsReopened)
        {
            text.Append("Инструменты снова доступны — работай, а не пиши итог.\n");
        }

        text.Append(escalated
            ? "Повторять то же действие бессмысленно: результат уже известен. Смени подход — перечитай ошибку, "
              + "выдвини другую гипотезу, проверь окружение, версии и зависимости, попробуй другой инструмент или "
              + "другой путь. Если все подходы, доступные в этой сессии, исчерпаны — назови конкретную блокирующую "
              + "причину и перечисли, что именно пробовал.\n"
            : "Исправь причину сбоя и запусти ту же команду заново до успешного результата. Если исправить "
              + "в этой сессии нельзя — прямо назови конкретную блокирующую причину и что уже пробовал.\n");

        text.Append("Не проси пользователя прислать «продолжай», не обещай сделать это следующим ходом и не "
            + "выдавай незавершённую работу за готовую.");
        return text.ToString();
    }

    /// <summary>Машинная приписка к ответу, когда бюджет продолжений исчерпан, а улики остались.</summary>
    /// <param name="evidence">Улики на момент завершения.</param>
    /// <returns>Готовая приписка или <c>null</c>, когда улик нет.</returns>
    public static string? BuildNote(IReadOnlyList<CompletionEvidence> evidence)
    {
        if (evidence.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(evidence.Count);
        foreach (var item in evidence)
        {
            parts.Add(item.Summary);
        }

        return "Проверено системой: ход завершился с неразрешёнными уликами ("
            + string.Join("; ", parts)
            + ") — это не подтверждённый готовый результат.";
    }

    /// <summary>Список улик для промпта: инструмент, что доказано, и хвост реального вывода.</summary>
    private static string Render(IReadOnlyList<CompletionEvidence> evidence)
    {
        var builder = new StringBuilder();
        foreach (var item in evidence)
        {
            builder.Append("- ").Append(item.Tool).Append(": ").Append(item.Summary).Append('\n');
            builder.Append("  ").Append(item.Detail.Replace("\n", "\n  ", StringComparison.Ordinal)).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Идентичность вызова: инструмент, аргументы и нормализованный вывод без служебных шумов.</summary>
    private static string KeyOf(string tool, string arguments, string normalizedOutput) =>
        tool + "\u0000" + arguments + "\u0000" + normalizedOutput;

    /// <summary>Хвост вывода, ограниченный <see cref="MaxEvidenceChars"/>.</summary>
    private static string Excerpt(string normalizedOutput)
    {
        if (normalizedOutput.Length <= MaxEvidenceChars)
        {
            return normalizedOutput;
        }

        return "…" + normalizedOutput[^MaxEvidenceChars..];
    }

    /// <summary>Самая свежая неудача, которую не снял успешный вызов того же инструмента.</summary>
    private CompletedCall? PendingFailure()
    {
        CompletedCall? latest = null;
        foreach (var call in _pendingFailures.Values)
        {
            if (latest is null || call.Sequence > latest.Sequence)
            {
                latest = call;
            }
        }

        return latest;
    }

    /// <summary>Серия идентичных вызовов, если она достигла порога.</summary>
    private CompletedCall? TrailingRepeat() =>
        _trailing.Count >= RepeatThreshold ? _trailing[0] : null;

    /// <summary>Один учтённый вызов: инструмент, идентичность, признак сбоя, хвост вывода и порядок.</summary>
    private sealed record CompletedCall(string Tool, string Key, bool Failed, string Excerpt, long Sequence);
}

using System.Text;
using ConexyAI.Contract;

namespace ConexyAI.Service;

// AUTO_COMPACT: добавлено 2026-10-18
/// <summary>
/// Пересказ длинного агентского прогона (как /compact в Claude Code). Сжатие выводов
/// (<see cref="AgentContextCompressor"/>) только обрезает старые tool-ответы, а на 100 шагах сама
/// цепочка шагов перестаёт помещаться в контекст. Когда прогон становится слишком большим, середина
/// (всё между началом задачи и последними шагами) заменяется одним пересказом от модели.
/// <para>
/// Граница выбирается только перед assistant-сообщением: пара assistant(tool_calls) → tool(...)
/// никогда не разрывается, иначе запрос к модели был бы невалидным.
/// </para>
/// </summary>
public static class AgentHistoryCompactor
{
    /// <summary>Порог размера прогона (символов): ~90 тыс. токенов при окне модели 128 тыс.</summary>
    public const int ThresholdChars = 360_000;

    /// <summary>Сколько последних сообщений оставляем как есть — по ним агент продолжает работу.</summary>
    public const int KeepTail = 14;

    private const int MaxOutputCharsInTranscript = 2_000;
    private const int MaxTranscriptChars = 120_000;

    public static int Size(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => ((m.Content as string)?.Length ?? 200) + (m.ReasoningContent?.Length ?? 0))
        + messages.Sum(m => m.ToolCalls?.Sum(c => c.Function.Arguments.Length + c.Function.Name.Length) ?? 0);

    /// <summary>
    /// Диапазон [start, end) для пересказа или null, если сжимать не нужно/нечего.
    /// <paramref name="runStart"/> — первое сообщение этого прогона после вопроса пользователя.
    /// </summary>
    public static (int Start, int End)? FindRange(IReadOnlyList<ChatMessage> messages, int runStart, int thresholdChars = ThresholdChars)
    {
        if (Size(messages) < thresholdChars)
            return null;
        var limit = messages.Count - KeepTail;
        for (var end = limit; end > runStart; end--)
        {
            if (messages[end].Role == "assistant")
                return end - runStart >= 4 ? (runStart, end) : null;
        }
        return null;
    }

    /// <summary>Текст середины прогона для модели-пересказчика: шаги, вызовы и обрезанные результаты.</summary>
    public static string Transcript(IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end && sb.Length < MaxTranscriptChars; i++)
        {
            var m = messages[i];
            var text = m.Content as string ?? string.Empty;
            switch (m.Role)
            {
                case "assistant":
                    if (text.Length > 0)
                        sb.Append("АГЕНТ: ").AppendLine(Cut(text, 3_000));
                    foreach (var call in m.ToolCalls ?? [])
                        sb.Append("ВЫЗОВ ").Append(call.Function.Name).Append(' ').AppendLine(Cut(call.Function.Arguments, 600));
                    break;
                case "tool":
                    sb.Append("РЕЗУЛЬТАТ: ").AppendLine(Cut(text, MaxOutputCharsInTranscript));
                    break;
                default:
                    if (text.Length > 0)
                        sb.Append(m.Role.ToUpperInvariant()).Append(": ").AppendLine(Cut(text, 2_000));
                    break;
            }
        }
        return sb.ToString();
    }

    public static readonly string Instruction =
        "Ты сжимаешь журнал работы ИИ-агента над задачей, чтобы он продолжил её без потери важного. " +
        "Напиши сжатый пересказ (до ~1200 слов) по разделам: 1) цель задачи; 2) что уже сделано и проверено; " +
        "3) изменённые и созданные файлы (пути) и суть изменений; 4) важные находки — команды сборки/запуска, " +
        "версии, пути, ошибки и как их решили; 5) что ещё не сделано и следующий шаг. Только факты из журнала, без вступлений.";

    /// <summary>Заменяет диапазон одним сообщением с пересказом.</summary>
    public static void Apply(List<ChatMessage> messages, int start, int end, string summary)
    {
        messages.RemoveRange(start, end - start);
        messages.Insert(start, new ChatMessage("user",
            "[Сжатый пересказ предыдущих шагов этого прогона — подробности опущены для экономии контекста. " +
            "Если нужны точные данные (содержимое файла, вывод команды), получи их заново инструментом.]\n" + summary.Trim()));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + " …";
}

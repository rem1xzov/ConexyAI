using System.Text;

namespace ConexyAI.Service;

// TEAM_FOLLOW_UP: добавлено 2026-11-20 (идея DeepSeek Harness, continuable subagents) — к завершённому
// помощнику можно вернуться с новым заданием: он получает свой прошлый отчёт и не повторяет сделанное.
// Это дешевле, чем запускать нового агента с нуля, и сохраняет контекст уже выполненной работы.
internal static class TeamFollowUp
{
    private const int MaxPreviousReportChars = 4_000;

    /// <summary>
    /// Задание для продолжения: новый запрос + прошлый отчёт помощника + запрет повторять сделанное.
    /// Пустой запрос возвращает пустую строку — вызывающий обязан отказать.
    /// </summary>
    public static string BuildBrief(string? previousReport, string? followUp)
    {
        var prompt = (followUp ?? string.Empty).Trim();
        if (prompt.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("Продолжение твоей работы: ").Append(prompt);
        if (!string.IsNullOrWhiteSpace(previousReport))
        {
            builder.Append("\n\nТвой прошлый отчёт (что уже сделано):\n")
                .Append(Truncate(previousReport.Trim(), MaxPreviousReportChars));
            builder.Append("\n\nНе повторяй уже выполненное: сделай новую часть и верни короткий отчёт.");
        }
        else
        {
            builder.Append("\n\nПрошлого отчёта нет: выполни задачу и верни короткий отчёт.");
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

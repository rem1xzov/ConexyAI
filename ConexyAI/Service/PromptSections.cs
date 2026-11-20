namespace ConexyAI.Service;

// PROMPT_SECTIONS: добавлено 2026-11-20 (идея DeepSeek Harness, core/system-prompt) — промпт агента
// собирается из упорядоченных секций с именами, а не из одной склеенной строки. Так у каждой части
// есть владелец и порядок, дубликаты имён отсекаются (одна секция — одно место), пустые не попадают в
// запрос, а стабильный порядок сохраняет кэш префикса.
public sealed record PromptSection(int Order, string Name, string Text);

public static class PromptComposer
{
    private const string TruncationMarker = "\n\n[... секция промпта обрезана по лимиту ...]";

    /// <summary>
    /// Детерминированный порядок: по <see cref="PromptSection.Order"/>, при равенстве — по имени.
    /// Пустые секции выбрасываются, одноимённые — только первая (защита от дублирования правил).
    /// </summary>
    public static IReadOnlyList<PromptSection> Order(IEnumerable<PromptSection> sections)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return sections
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .Where(section => seen.Add(section.Name))
            .OrderBy(section => section.Order)
            .ThenBy(section => section.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Склеивает секции в текст; при заданном лимите обрезает и оставляет явный маркер.</summary>
    public static string Compose(IEnumerable<PromptSection> sections, int maxChars = 0)
    {
        var ordered = Order(sections);
        if (ordered.Count == 0)
            return string.Empty;

        var text = string.Join("\n\n", ordered.Select(section => section.Text.Trim()));
        if (maxChars > 0 && text.Length > maxChars)
            text = text[..Math.Max(0, maxChars - TruncationMarker.Length)] + TruncationMarker;
        return text;
    }
}

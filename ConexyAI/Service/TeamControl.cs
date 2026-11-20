namespace ConexyAI.Service;

// TEAM_CONTROL: добавлено 2026-11-20 (идея DeepSeek Harness, tool-subagent-control) — ведущий может
// не только ждать помощников, но и точечно останавливать одного, не убивая остальную команду.
// Логика выбора роли вынесена из раннера, чтобы её можно было проверить тестами без прогона модели.
internal static class TeamControl
{
    /// <summary>Стабильное имя помощника по его порядковому номеру в команде.</summary>
    public static string NameFor(int index) => $"worker-{index + 1}";

    /// <summary>
    /// Находит роль для остановки: точное совпадение, затем без учёта регистра/пробелов.
    /// <c>null</c> — такой роли в команде нет.
    /// </summary>
    public static string? ResolveRole(IEnumerable<string> knownRoles, string? requested)
    {
        var wanted = requested?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return null;

        var exact = knownRoles.FirstOrDefault(role => string.Equals(role, wanted, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        return knownRoles.FirstOrDefault(role => string.Equals(role, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Текст отказа, когда роли нет: перечисляет, кого действительно можно остановить.</summary>
    public static string Refusal(IReadOnlyCollection<string> knownRoles, string? requested) =>
        $"Помощника '{requested?.Trim()}' в команде нет. Доступные роли: " +
        (knownRoles.Count == 0 ? "команда пуста" : string.Join(", ", knownRoles)) + ".";

    /// <summary>Строка статуса одного помощника: роль, стабильное имя и состояние.</summary>
    public static string StatusLine(string role, string name, string state) => $"## {role} ({name}): {state}";
}

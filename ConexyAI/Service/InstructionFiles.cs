namespace ConexyAI.Service;

// INSTRUCTION_DIGEST: добавлено 2026-11-20 — правила проекта перечитываются раз в 5 шагов, но стресс-тест
// показал: клиент получал запрос правил даже когда менялись только исходники. Файлы правил и хуков
// меняются редко, поэтому обновление нужно ТОЛЬКО когда среди изменённых файлов есть один из них.
// Хук также сравнивает хвост пути: агент называет файл то относительно корня, то полным путём.
internal static class InstructionFiles
{
    /// <summary>Файлы, которые реально влияют на промпт прогона (зеркало ProjectRuleFiles + hooks).</summary>
    private static readonly string[] Files =
    [
        "CONEXY.md",
        "CLAUDE.md",
        "AGENTS.md",
        ".conexy/rules.md",
        ".conexy/hooks.json",
    ];

    /// <summary>Изменился ли хотя бы один файл правил/хуков среди изменённых за прогон.</summary>
    public static bool Touched(IEnumerable<string>? changedFiles) =>
        changedFiles is not null && changedFiles.Any(IsInstructionFile);

    /// <summary>Путь — файл правил/хуков (сравнение по хвосту, регистр и слэши не важны).</summary>
    public static bool IsInstructionFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var norm = path.Replace('\\', '/').Trim();
        while (norm.StartsWith("./", StringComparison.Ordinal))
            norm = norm[2..];
        norm = norm.TrimStart('/');

        return Files.Any(file =>
            norm.Equals(file, StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase));
    }
}

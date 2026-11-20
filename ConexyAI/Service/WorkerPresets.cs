namespace ConexyAI.Service;

// WORKER_PRESETS: добавлено 2026-11-20 (идея DeepSeek Harness, packages/preset) — набор инструментов
// помощника задаётся пресетом, а не только ролью в тексте задания. Читающий ресёрчер и проверяющий
// ревьюер не должны получать права на правку файлов и команды: это и безопаснее, и дешевле по промпту.
// Пресет ограничивает только инструменты; глубина субагентов и бюджеты остаются общими.
public enum WorkerPreset
{
    /// <summary>Помощник с полным набором инструментов Coder (по умолчанию).</summary>
    Default,

    /// <summary>Только чтение и поиск: файлы, grep/glob, карта проекта, веб, документы. Без правок и команд.</summary>
    Researcher,

    /// <summary>Чтение + запуск тестов и диагностики: проверяет чужую работу, но ничего не меняет.</summary>
    Reviewer,
}

public static class WorkerPresets
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "file_read",
        "file_read_bytes",
        "workspace_list_files",
        "grep",
        "glob",
        "repo_map",
        "view_image",
        "read_document_file",
        "read_document_chunk",
        "search_documents",
        "search_user_chats",
        "web_search",
        "fetch_web_page",
        "todo_write",
        "read_skill",
    };

    /// <summary>Инструменты пресета; <c>null</c> — все инструменты (пресет по умолчанию).</summary>
    public static IReadOnlySet<string>? AllowedTools(WorkerPreset preset) => preset switch
    {
        WorkerPreset.Researcher => ReadOnlyTools,
        WorkerPreset.Reviewer => new HashSet<string>(ReadOnlyTools, StringComparer.Ordinal)
        {
            "run_tests",
            "get_diagnostics",
        },
        _ => null,
    };

    /// <summary>Строка для промпта помощника: что именно ему разрешено.</summary>
    public static string Describe(WorkerPreset preset) => preset switch
    {
        WorkerPreset.Researcher =>
            "режим researcher: только чтение и поиск (файлы, grep/glob, repo_map, веб, документы). " +
            "Ты НЕ меняешь файлы и не запускаешь команды — собери факты и верни отчёт.",
        WorkerPreset.Reviewer =>
            "режим reviewer: чтение и проверка. Ты можешь смотреть файлы и запускать run_tests/get_diagnostics, " +
            "но НЕ меняешь код — верни список найденных проблем с путями и строками.",
        _ => "полный набор инструментов (как у Coder).",
    };

    /// <summary>Разбор значения из схемы spawn_agents; неизвестное значение — пресет по умолчанию.</summary>
    public static WorkerPreset Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "researcher" or "research" or "readonly" => WorkerPreset.Researcher,
        "reviewer" or "review" or "qa" => WorkerPreset.Reviewer,
        _ => WorkerPreset.Default,
    };
}

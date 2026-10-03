namespace ConexyAI.Entity;

// USER_PREFERENCES: добавлено 2026-09-24 — пользовательские инструкции (ТЗ 2, §5) и отключение памяти
// (ревью H5). Отдельная таблица, а не колонки в users: настройки общения не имеют отношения к
// аутентификации, и строка появляется только у тех, кто их менял.
/// <summary>Per-user personalization: custom instructions and the memory switch.</summary>
public class UserPreferencesEntity
{
    public Guid UserId { get; set; }

    // USER_NAME: добавлено 2026-10-01 — как приложение обращается к человеку (приветствие на старте).
    // Пусто — имя выводится из email, как раньше.
    /// <summary>Preferred name; empty means "derive from the email".</summary>
    public string? Name { get; set; }

    /// <summary>"About me": stack, role, preferences — injected into every mode's system prompt.</summary>
    public string? AboutMe { get; set; }

    /// <summary>"How ConexyAI should respond": length, tone, language, formatting.</summary>
    public string? ResponseStyle { get; set; }

    /// <summary>When false nothing is extracted into long-term memory and nothing is injected.</summary>
    public bool MemoryEnabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

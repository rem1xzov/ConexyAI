namespace ConexyAI.Entity;

// AGENT_SKILLS: добавлено 2026-10-18
/// <summary>
/// Скилл пользователя — инструкция для агента в формате SKILL.md (как в Claude): имя, описание
/// «когда применять» и сам текст. В промпт идут только имя и описание; полный текст агент
/// подгружает инструментом read_skill, когда задача подходит. Общие для веба и десктопа.
/// </summary>
public class UserSkill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>Короткое имя-идентификатор (латиница, цифры, дефис), уникально у пользователя.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Когда агенту стоит применять скилл (из frontmatter description).</summary>
    public string Description { get; set; } = null!;

    /// <summary>Полный текст SKILL.md (без frontmatter).</summary>
    public string Content { get; set; } = null!;

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}

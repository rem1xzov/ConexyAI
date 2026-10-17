using System.Text;
using System.Text.RegularExpressions;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service;

// AGENT_SKILLS: добавлено 2026-10-18
public record SkillDto(Guid Id, string Name, string Description, bool Enabled, int Chars, DateTime UpdatedAt);

public record SkillSummary(string Name, string Description);

public interface ISkillService
{
    Task<IReadOnlyList<SkillDto>> ListAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Добавляет скилл из файла SKILL.md; скилл с тем же именем заменяется.</summary>
    Task<SkillDto> UpsertFromFileAsync(Guid userId, string fileName, string text, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<SkillDto?> SetEnabledAsync(Guid userId, Guid id, bool enabled, CancellationToken ct = default);
    /// <summary>Включённые скиллы для промпта агента (имя + когда применять).</summary>
    Task<IReadOnlyList<SkillSummary>> EnabledSummariesAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Полный текст включённого скилла по имени (для read_skill).</summary>
    Task<string?> ReadAsync(Guid userId, string name, CancellationToken ct = default);
}

public partial class SkillService : ISkillService
{
    public const int MaxSkills = 30;
    public const int MaxChars = 60_000;

    private readonly DbConexy _db;

    public SkillService(DbConexy db) => _db = db;

    public async Task<IReadOnlyList<SkillDto>> ListAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserSkills.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Name)
            .Select(s => new SkillDto(s.Id, s.Name, s.Description, s.Enabled, s.Content.Length, s.UpdatedAt))
            .ToListAsync(ct);

    public async Task<SkillDto> UpsertFromFileAsync(Guid userId, string fileName, string text, CancellationToken ct = default)
    {
        var (name, description, content) = Parse(fileName, text);
        var existing = await _db.UserSkills.FirstOrDefaultAsync(s => s.UserId == userId && s.Name == name, ct);
        if (existing is null)
        {
            if (await _db.UserSkills.CountAsync(s => s.UserId == userId, ct) >= MaxSkills)
                throw new AuthException("too_many_skills", $"Можно хранить до {MaxSkills} скиллов. Удалите ненужные.", 409);
            existing = new UserSkill { UserId = userId, Name = name };
            _db.UserSkills.Add(existing);
        }
        existing.Description = description;
        existing.Content = content;
        existing.Enabled = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new SkillDto(existing.Id, existing.Name, existing.Description, existing.Enabled, existing.Content.Length, existing.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var skill = await _db.UserSkills.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (skill is null)
            return false;
        _db.UserSkills.Remove(skill);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SkillDto?> SetEnabledAsync(Guid userId, Guid id, bool enabled, CancellationToken ct = default)
    {
        var skill = await _db.UserSkills.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (skill is null)
            return null;
        skill.Enabled = enabled;
        skill.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new SkillDto(skill.Id, skill.Name, skill.Description, skill.Enabled, skill.Content.Length, skill.UpdatedAt);
    }

    public async Task<IReadOnlyList<SkillSummary>> EnabledSummariesAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserSkills.AsNoTracking()
            .Where(s => s.UserId == userId && s.Enabled)
            .OrderBy(s => s.Name)
            .Select(s => new SkillSummary(s.Name, s.Description))
            .ToListAsync(ct);

    public async Task<string?> ReadAsync(Guid userId, string name, CancellationToken ct = default)
    {
        var clean = Slug(name);
        return await _db.UserSkills.AsNoTracking()
            .Where(s => s.UserId == userId && s.Enabled && s.Name == clean)
            .Select(s => s.Content)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Разбор SKILL.md: frontmatter (--- name / description ---) как у Claude Skills. Без него имя берётся
    /// из имени файла, описание — из первого абзаца после заголовка.
    /// </summary>
    public static (string Name, string Description, string Content) Parse(string fileName, string text)
    {
        text = (text ?? string.Empty).Replace("\r\n", "\n").TrimStart('\uFEFF');
        if (text.Contains('\0'))
            throw new AuthException("skill_binary", "Скилл должен быть текстовым файлом (SKILL.md).", 400);
        if (text.Trim().Length == 0)
            throw new AuthException("skill_empty", "Файл скилла пустой.", 400);
        if (text.Length > MaxChars)
            throw new AuthException("skill_too_large", $"Скилл длиннее {MaxChars / 1000} тыс. символов — сократите его.", 400);

        string? name = null, description = null;
        var body = text;
        var front = Frontmatter().Match(text);
        if (front.Success)
        {
            body = text[front.Length..];
            foreach (var line in front.Groups[1].Value.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (key == "name") name = value;
                else if (key == "description") description = value;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(fileName ?? "skill");
        if (stem.Equals("skill", StringComparison.OrdinalIgnoreCase) || stem.Length == 0)
            stem = Heading().Match(body) is { Success: true } h ? h.Groups[1].Value : "skill";
        name = Slug(string.IsNullOrWhiteSpace(name) ? stem : name);

        if (string.IsNullOrWhiteSpace(description))
        {
            // Первый абзац обычного текста после заголовков.
            description = body.Split("\n\n")
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.Length > 0 && !p.StartsWith('#') && !p.StartsWith("```"))
                ?? $"Инструкция «{name}»";
        }
        description = string.Join(' ', description.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (description.Length > 1000)
            description = description[..1000] + "…";

        return (name, description, body.Trim());
    }

    /// <summary>Имя скилла: латиница/цифры/дефис, до 64 символов (кириллица транслитерируется).</summary>
    public static string Slug(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (Translit.TryGetValue(ch, out var t)) sb.Append(t);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 64) slug = slug[..64].TrimEnd('-');
        return slug.Length == 0 ? "skill" : slug;
    }

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ы'] = "y", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    [GeneratedRegex(@"^---\n(.*?)\n---[ \t]*\n?", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex Heading();
}

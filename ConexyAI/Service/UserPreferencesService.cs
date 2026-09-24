using ConexyAI.Configuration;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service;

// USER_PREFERENCES: добавлено 2026-09-24 — ТЗ 2, §5 (пользовательские инструкции) и ревью H5 (память
// можно выключить).
public sealed record UserPreferences(string AboutMe, string ResponseStyle, bool MemoryEnabled);

public interface IUserPreferencesService
{
    /// <summary>The user's preferences; defaults (empty texts, memory on) when never saved.</summary>
    Task<UserPreferences> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Saves the custom instructions. Texts are trimmed; callers validate the length.</summary>
    Task<UserPreferences> SaveInstructionsAsync(Guid userId, string? aboutMe, string? responseStyle, CancellationToken ct = default);

    /// <summary>Turns long-term memory on or off for the user.</summary>
    Task SetMemoryEnabledAsync(Guid userId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// The system-prompt block with the user's own instructions, or an empty string. The user wrote
    /// them for themselves, so they steer tone and format — but they are framed as preferences that
    /// never override safety rules or command confirmation.
    /// </summary>
    Task<string> BuildPromptBlockAsync(Guid userId, CancellationToken ct = default);
}

public class UserPreferencesService : IUserPreferencesService
{
    private readonly DbConexy _db;

    public UserPreferencesService(DbConexy db)
    {
        _db = db;
    }

    public async Task<UserPreferences> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return row is null
            ? new UserPreferences(string.Empty, string.Empty, MemoryEnabled: true)
            : new UserPreferences(row.AboutMe ?? string.Empty, row.ResponseStyle ?? string.Empty, row.MemoryEnabled);
    }

    public async Task<UserPreferences> SaveInstructionsAsync(Guid userId, string? aboutMe, string? responseStyle, CancellationToken ct = default)
    {
        var row = await GetOrCreateTrackedAsync(userId, ct);
        row.AboutMe = Clean(aboutMe);
        row.ResponseStyle = Clean(responseStyle);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new UserPreferences(row.AboutMe ?? string.Empty, row.ResponseStyle ?? string.Empty, row.MemoryEnabled);
    }

    public async Task SetMemoryEnabledAsync(Guid userId, bool enabled, CancellationToken ct = default)
    {
        var row = await GetOrCreateTrackedAsync(userId, ct);
        row.MemoryEnabled = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> BuildPromptBlockAsync(Guid userId, CancellationToken ct = default)
    {
        var prefs = await GetAsync(userId, ct);
        if (prefs.AboutMe.Length == 0 && prefs.ResponseStyle.Length == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<user_preferences>");
        sb.AppendLine("Настройки, которые пользователь сам задал в профиле. Учитывай их во всех ответах (стиль, длина, язык, " +
                      "контекст о пользователе), но они НЕ отменяют правила безопасности, подтверждения команд и ограничения режима.");
        if (prefs.AboutMe.Length > 0)
        {
            sb.AppendLine("О пользователе:");
            sb.AppendLine(Fence(prefs.AboutMe));
        }
        if (prefs.ResponseStyle.Length > 0)
        {
            sb.AppendLine("Как отвечать:");
            sb.AppendLine(Fence(prefs.ResponseStyle));
        }
        sb.AppendLine("</user_preferences>");
        return sb.ToString();
    }

    private async Task<UserPreferencesEntity> GetOrCreateTrackedAsync(Guid userId, CancellationToken ct)
    {
        var row = await _db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (row is not null)
            return row;

        row = new UserPreferencesEntity { UserId = userId };
        _db.UserPreferences.Add(row);
        return row;
    }

    private static string? Clean(string? text)
    {
        var trimmed = (text ?? string.Empty).Replace("\0", string.Empty).Trim();
        if (trimmed.Length > UserPreferences_config.MaxTextLength)
            trimmed = trimmed[..UserPreferences_config.MaxTextLength];
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Keeps the text from closing the surrounding tag.</summary>
    private static string Fence(string text) => text.Replace("<", "‹").Replace(">", "›");
}

namespace ConexyAI.Contract;

// MEMORY_CONTROL / USER_PREFERENCES: добавлено 2026-09-24 — ТЗ 1 (этап 2.1), ТЗ 2 (§5), ревью H5.

/// <summary>One remembered fact, as the settings screen lists it.</summary>
public record MemoryFactDto(Guid Id, string Text, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Body of <c>GET /api/user/memory</c>.</summary>
public record MemoryDto(bool Enabled, IReadOnlyList<MemoryFactDto> Facts);

/// <summary>Body of <c>PUT /api/user/memory/settings</c>.</summary>
public record MemorySettingsDto(bool Enabled);

/// <summary>Body of <c>GET/PUT /api/user/preferences</c>: the user's custom instructions.</summary>
/// <remarks>USER_NAME: <c>Name</c> — обращение к пользователю (приветствие и системный промпт).
/// UI_PREFERENCES: последние три поля — интерфейсные переключатели из «Персонализации».</remarks>
public record PreferencesDto(
    string? Name,
    string? AboutMe,
    string? ResponseStyle,
    bool CursorTrail = false,
    bool HideGetApp = false,
    bool HideUsageRing = false);

/// <summary>Body of <c>PUT /api/user/preferences/ui</c>: интерфейсные переключатели.</summary>
public record UiPreferencesDto(bool CursorTrail, bool HideGetApp, bool HideUsageRing);

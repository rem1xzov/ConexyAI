namespace ConexyAI.Contract;

// MEMORY_CONTROL / USER_PREFERENCES: добавлено 2026-09-24 — ТЗ 1 (этап 2.1), ТЗ 2 (§5), ревью H5.

/// <summary>One remembered fact, as the settings screen lists it.</summary>
public record MemoryFactDto(Guid Id, string Text, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Body of <c>GET /api/user/memory</c>.</summary>
public record MemoryDto(bool Enabled, IReadOnlyList<MemoryFactDto> Facts);

/// <summary>Body of <c>PUT /api/user/memory/settings</c>.</summary>
public record MemorySettingsDto(bool Enabled);

/// <summary>Body of <c>GET/PUT /api/user/preferences</c>: the user's custom instructions.</summary>
public record PreferencesDto(string? AboutMe, string? ResponseStyle);

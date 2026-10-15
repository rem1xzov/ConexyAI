namespace ConexyAI.Entity;

// DESKTOP_AGENT: добавлено 2026-10-15 — ключ десктопного клиента. Сам ключ показывается один раз
// при выпуске; в БД лежит только SHA-256 хэш, поэтому утечка БД не раскрывает рабочие ключи.
public class DeviceApiKeyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Владелец ключа. Тариф и токены берутся из его подписки.</summary>
    public Guid UserId { get; set; }

    /// <summary>Человекочитаемое имя («Рабочий ноутбук»), чтобы отличать ключи в списке.</summary>
    public string Name { get; set; } = null!;

    /// <summary>SHA-256 ключа в hex (64 символа). Уникален.</summary>
    public string KeyHash { get; set; } = null!;

    /// <summary>Первые символы ключа — только для опознания в списке.</summary>
    public string KeyPrefix { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    /// <summary>Не null — ключ отозван и больше не меняется на токен.</summary>
    public DateTime? RevokedAt { get; set; }
}

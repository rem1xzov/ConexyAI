namespace ConexyAI.Entity;

// PHONE_AGENT: добавлено 2026-10-29 — FCM-токен устройства пользователя (Android-приложение).
public class MobilePushTokenEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>FCM registration token. Уникален: один токен — один владелец.</summary>
    public string Token { get; set; } = null!;

    public string Platform { get; set; } = "android";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

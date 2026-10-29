namespace ConexyAI.Configuration;

// PHONE_AGENT: добавлено 2026-10-29 — push-уведомления на телефон (FCM HTTP v1, только data-сообщения).
public sealed class PushOptions
{
    public const string SectionName = "Push";

    public FcmOptions Fcm { get; set; } = new();

    public sealed class FcmOptions
    {
        /// <summary>Id проекта Firebase (Push:Fcm:ProjectId).</summary>
        public string? ProjectId { get; set; }

        /// <summary>Содержимое JSON-ключа сервисного аккаунта ИЛИ путь к файлу (Push:Fcm:ServiceAccountJson).</summary>
        public string? ServiceAccountJson { get; set; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(ServiceAccountJson);
    }
}

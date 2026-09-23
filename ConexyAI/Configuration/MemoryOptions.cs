namespace ConexyAI.Configuration;

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
/// <summary>
/// Binds the <c>Memory</c> section of appsettings.json. Controls the background user-memory
/// summarization: how many user messages per chat trigger an extraction run and how many
/// recent messages are fed to the extraction prompt.
/// </summary>
public class MemoryOptions
{
    public const string SectionName = "Memory";

    /// <summary>
    /// Extract memory after the first user message of a chat and then after every this many user
    /// messages (default 3).
    /// <para>
    /// CROSS_CHAT_CONTEXT: изменено 2026-09-23. Раньше извлечение шло ТОЛЬКО на каждом 7-м сообщении,
    /// поэтому чат короче 7 сообщений вообще ничего не оставлял в памяти, и новый чат не знал даже
    /// имени пользователя (проверено сквозным тестом: факты появлялись только после 7-го сообщения).
    /// </para>
    /// </summary>
    public int BatchingThreshold { get; set; } = 3;

    /// <summary>How many recent dialog messages to pass to the extraction prompt.</summary>
    public int RecentMessagesToReview { get; set; } = 30;
}

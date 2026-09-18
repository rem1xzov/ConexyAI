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

    /// <summary>Extract memory after this many user messages in a single chat (default 7).</summary>
    public int BatchingThreshold { get; set; } = 7;

    /// <summary>How many recent dialog messages to pass to the extraction prompt.</summary>
    public int RecentMessagesToReview { get; set; } = 30;
}

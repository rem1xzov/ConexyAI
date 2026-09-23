namespace ConexyAI.Configuration;

// CONVERSATION_SERVICE: добавлено 2026-09-23
/// <summary>
/// Binds the <c>Conversation</c> section of appsettings.json. Holds the single history-depth
/// setting used by <see cref="Service.IConversationService"/> when it assembles a model request —
/// previously the "last 20 messages" limit was duplicated as a private constant in both the chat
/// worker and the agent runner, which is exactly how the two paths drifted apart.
/// </summary>
public class ConversationOptions
{
    public const string SectionName = "Conversation";

    /// <summary>
    /// How many of the most recent history rows are attached to a request. Callers that need a
    /// different depth (e.g. the long-term memory extractor) pass it explicitly instead of
    /// hard-coding their own value.
    /// </summary>
    public int HistoryDepth { get; set; } = 20;

    // CROSS_CHAT_CONTEXT: добавлено 2026-09-23
    /// <summary>
    /// How many of the user's other recent chats are summarized into every request (start of the
    /// chat and the last answer), so any model can pick up "what we discussed in the other chat".
    /// Before this, the only thing that crossed chats was the extracted memory facts. <c>0</c> disables it.
    /// </summary>
    public int RecentChatsInContext { get; set; } = 5;
}

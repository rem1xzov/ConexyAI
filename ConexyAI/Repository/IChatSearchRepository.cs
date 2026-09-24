namespace ConexyAI.Repository;

// SEARCH_USER_CHATS: добавлено 2026-09-24
/// <summary>
/// Read-only keyword search over one user's stored conversations, backing the agents'
/// <c>search_user_chats</c> tool ("как мы настраивали Nginx в прошлом чате?").
/// <para>
/// Separate from <see cref="IChatHistoryRepository"/> on purpose: that repository owns writing and
/// replaying history and is reserved for the conversation service, while this one only ever reads,
/// and only for the user id the caller passes — which must be the id of the running job, never a
/// value taken from the model's tool arguments. Incognito chats are never stored in this table, so
/// they can never be found here.
/// </para>
/// </summary>
public interface IChatSearchRepository
{
    /// <summary>
    /// Chats of <paramref name="userId"/> (other than <paramref name="excludeChatId"/>) whose messages
    /// match <paramref name="query"/>, best match first.
    /// </summary>
    /// <param name="maxChats">1..10; values outside are clamped.</param>
    Task<ChatSearchResult> SearchAsync(
        Guid userId,
        Guid excludeChatId,
        string query,
        int maxChats,
        CancellationToken ct = default);
}

/// <summary>Search outcome: the terms actually searched and the matching chats.</summary>
public sealed record ChatSearchResult(IReadOnlyList<string> Terms, IReadOnlyList<ChatSearchHit> Chats);

/// <summary>One matching chat.</summary>
/// <param name="Title">User-given name, or the start of the first user message.</param>
/// <param name="LastActivityAt">Time of the chat's newest message (UTC).</param>
/// <param name="MatchedTerms">Which of the searched terms occur in this chat.</param>
/// <param name="Snippets">1–3 excerpts around the hits.</param>
public sealed record ChatSearchHit(
    Guid ChatId,
    string Title,
    DateTime LastActivityAt,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<ChatSearchSnippet> Snippets);

/// <summary>An excerpt of one message; <paramref name="Role"/> is <c>user</c> or <c>assistant</c>.</summary>
public sealed record ChatSearchSnippet(string Role, DateTime CreatedAt, string Text);

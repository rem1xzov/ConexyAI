using System.Collections.Concurrent;
using ConexyAI.Entity;

namespace ConexyAI.Service;

// INCOGNITO_CHAT: добавлено 2026-09-20
/// <summary>
/// Ephemeral, in-memory conversation history for incognito chats. Nothing is written to
/// <c>conexy_chat_message</c>: the turns live in this singleton only, so the dialog keeps its
/// context across messages of the same incognito session but disappears with the process (or
/// once the thread goes idle) and can never reach the long-term-memory extractor.
/// </summary>
public interface IIncognitoChatStore
{
    /// <summary>Prior turns of an incognito thread, oldest first. Empty when unknown or expired.</summary>
    IReadOnlyList<ConexyChatMessageEntity> GetMessages(Guid chatId);

    /// <summary>Adds a turn to the in-memory thread (never touches the database).</summary>
    void Append(Guid chatId, Guid userId, string role, string content);

    /// <summary>Drops a thread — used when the user leaves the incognito chat.</summary>
    void Clear(Guid chatId);
}

public class IncognitoChatStore : IIncognitoChatStore
{
    // An abandoned incognito thread must not sit in memory forever, and a single thread must
    // not grow without bound. Both limits only affect very long-lived/idle sessions.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
    private const int MaxMessagesPerThread = 40;

    private readonly ConcurrentDictionary<Guid, Thread> _threads = new();

    private sealed class Thread
    {
        public readonly object Gate = new();
        public readonly List<ConexyChatMessageEntity> Messages = new();
        public DateTime LastTouched = DateTime.UtcNow;
    }

    public IReadOnlyList<ConexyChatMessageEntity> GetMessages(Guid chatId)
    {
        Prune();
        if (!_threads.TryGetValue(chatId, out var thread))
            return Array.Empty<ConexyChatMessageEntity>();

        lock (thread.Gate)
        {
            thread.LastTouched = DateTime.UtcNow;
            return thread.Messages.ToList();
        }
    }

    public void Append(Guid chatId, Guid userId, string role, string content)
    {
        Prune();
        var thread = _threads.GetOrAdd(chatId, _ => new Thread());
        lock (thread.Gate)
        {
            thread.Messages.Add(new ConexyChatMessageEntity
            {
                ChatId = chatId,
                UserId = userId,
                Role = role,
                Content = content
            });

            if (thread.Messages.Count > MaxMessagesPerThread)
                thread.Messages.RemoveRange(0, thread.Messages.Count - MaxMessagesPerThread);

            thread.LastTouched = DateTime.UtcNow;
        }
    }

    public void Clear(Guid chatId) => _threads.TryRemove(chatId, out _);

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (id, thread) in _threads)
        {
            if (thread.LastTouched < cutoff)
                _threads.TryRemove(id, out _);
        }
    }
}

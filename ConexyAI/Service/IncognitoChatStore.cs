using System.Collections.Concurrent;
using ConexyAI.Entity;

namespace ConexyAI.Service;

// INCOGNITO_CHAT: добавлено 2026-09-20
/// <summary>
/// Ephemeral, in-memory conversation history for incognito chats. Nothing is written to
/// <c>conexy_chat_message</c>: the turns live in this singleton only, so the dialog keeps its
/// context across messages of the same incognito session but disappears with the process (or
/// once the thread goes idle) and can never reach the long-term-memory extractor.
/// <para>
/// CHAT_OWNERSHIP: изменено 2026-09-24 (ревью M7). Раньше тред ключевался только chatId, и любой,
/// кто знал id, читал чужой инкогнито-диалог. Теперь у треда есть владелец: чтение, запись и
/// удаление проверяют его, а просроченные треды отдаются наружу, чтобы их файлы удалялись с диска.
/// </para>
/// </summary>
public interface IIncognitoChatStore
{
    /// <summary>
    /// Registers <paramref name="userId"/> as the owner of an incognito thread. True when the thread is
    /// new or already belongs to that user; false when another user owns it.
    /// </summary>
    bool TryClaim(Guid chatId, Guid userId);

    /// <summary>Owner of a live incognito thread, or null when there is none.</summary>
    Guid? GetOwner(Guid chatId);

    /// <summary>Prior turns of the user's incognito thread, oldest first. Empty when unknown, expired or foreign.</summary>
    IReadOnlyList<ConexyChatMessageEntity> GetMessages(Guid chatId, Guid userId);

    /// <summary>Adds a turn to the in-memory thread (never touches the database). Ignored for a foreign thread.</summary>
    void Append(Guid chatId, Guid userId, string role, string content);

    /// <summary>Drops the user's thread. False when there was no such thread of this user.</summary>
    bool Clear(Guid chatId, Guid userId);

    /// <summary>Removes idle threads and returns their chat ids, so the caller can delete their files.</summary>
    IReadOnlyList<Guid> PruneExpired();
}

public class IncognitoChatStore : IIncognitoChatStore
{
    // An abandoned incognito thread must not sit in memory forever, and a single thread must
    // not grow without bound. Both limits only affect very long-lived/idle sessions.
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
    private const int MaxMessagesPerThread = 40;

    private readonly ConcurrentDictionary<Guid, Thread> _threads = new();

    private sealed class Thread
    {
        public Thread(Guid ownerId) => OwnerId = ownerId;

        public readonly Guid OwnerId;
        public readonly object Gate = new();
        public readonly List<ConexyChatMessageEntity> Messages = new();
        public DateTime LastTouched = DateTime.UtcNow;
    }

    public bool TryClaim(Guid chatId, Guid userId)
    {
        var thread = _threads.GetOrAdd(chatId, _ => new Thread(userId));
        if (thread.OwnerId != userId)
            return false;

        lock (thread.Gate)
        {
            thread.LastTouched = DateTime.UtcNow;
        }
        return true;
    }

    public Guid? GetOwner(Guid chatId) =>
        _threads.TryGetValue(chatId, out var thread) ? thread.OwnerId : null;

    public IReadOnlyList<ConexyChatMessageEntity> GetMessages(Guid chatId, Guid userId)
    {
        if (!_threads.TryGetValue(chatId, out var thread) || thread.OwnerId != userId)
            return Array.Empty<ConexyChatMessageEntity>();

        lock (thread.Gate)
        {
            thread.LastTouched = DateTime.UtcNow;
            return thread.Messages.ToList();
        }
    }

    public void Append(Guid chatId, Guid userId, string role, string content)
    {
        var thread = _threads.GetOrAdd(chatId, _ => new Thread(userId));
        if (thread.OwnerId != userId)
            return;

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

    public bool Clear(Guid chatId, Guid userId)
    {
        if (!_threads.TryGetValue(chatId, out var thread) || thread.OwnerId != userId)
            return false;

        return _threads.TryRemove(new KeyValuePair<Guid, Thread>(chatId, thread));
    }

    public IReadOnlyList<Guid> PruneExpired()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        var expired = new List<Guid>();
        foreach (var (id, thread) in _threads)
        {
            if (thread.LastTouched < cutoff && _threads.TryRemove(new KeyValuePair<Guid, Thread>(id, thread)))
                expired.Add(id);
        }
        return expired;
    }
}

using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service;

// CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1 (IDOR).
//
// Раньше всё, что ключуется одним chatId (воркспейс на диске, IDE-эндпоинты, группа SignalR
// task_{chatId}), вообще не знало владельца: контроллеры вызывали TryGetUserId(out _) и выбрасывали
// id. Chat id при этом не секрет — «Поделиться» раздаёт его в ссылке. Этот сервис — единственная
// точка, которая отвечает на вопрос «чей это чат», и каждый вход в чат-ресурсы идёт через него.

/// <summary>Who a chat id belongs to, from the caller's point of view.</summary>
public enum ChatAccessKind
{
    /// <summary>The caller owns the chat.</summary>
    Owner,

    /// <summary>Another user owns the chat (or it was deleted): refuse.</summary>
    Forbidden,

    /// <summary>
    /// Nobody owns the id yet and nothing is stored under it: a brand-new chat. Reads see an empty
    /// chat; the first write or run claims it.
    /// </summary>
    Unclaimed,
}

/// <summary>Thrown when a caller touches a chat or task that is not theirs. Mapped to 403.</summary>
public sealed class ChatAccessDeniedException : Exception
{
    public ChatAccessDeniedException(Guid id)
        : base($"Access to '{id}' is forbidden.")
    {
        Id = id;
    }

    public Guid Id { get; }
}

public interface IChatAccessService
{
    /// <summary>Resolves ownership of <paramref name="chatId"/> for <paramref name="userId"/>.</summary>
    Task<ChatAccessKind> GetAccessAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// For read paths: true when the caller owns the chat, or when the id is unclaimed and has no
    /// workspace on disk (an empty, brand-new chat — there is nothing to leak).
    /// </summary>
    Task<bool> CanReadAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// For write paths and new turns: returns when the caller owns the chat, claiming an unclaimed
    /// id on the way; throws <see cref="ChatAccessDeniedException"/> otherwise. Incognito turns are
    /// claimed in memory only, so they leave no trace in the database.
    /// </summary>
    Task EnsureWritableAsync(
        Guid userId,
        Guid chatId,
        bool incognito = false,
        string? kind = null,
        string? model = null,
        CancellationToken ct = default);

    /// <summary>True when <paramref name="taskId"/> is a task (turn) row of <paramref name="userId"/>.</summary>
    Task<bool> IsTaskOwnerAsync(Guid userId, Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// SignalR groups are named after either a task id or a chat id. True when the caller may listen
    /// to <c>task_{id}</c>: their own task, their own chat, or an id nobody owns yet.
    /// </summary>
    Task<bool> CanJoinScopeAsync(Guid userId, Guid id, CancellationToken ct = default);

    /// <summary>Marks the chat deleted (tombstone). No-op when the caller does not own it.</summary>
    Task MarkDeletedAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>True when the owner deleted this chat. Used to stop an in-flight turn resurrecting it.</summary>
    Task<bool> IsDeletedAsync(Guid chatId, CancellationToken ct = default);
}

public class ChatAccessService : IChatAccessService
{
    private readonly DbConexy _db;
    private readonly IConexyWorkspaceService _workspace;
    private readonly IIncognitoChatStore _incognito;
    private readonly ILogger<ChatAccessService> _logger;

    public ChatAccessService(
        DbConexy db,
        IConexyWorkspaceService workspace,
        IIncognitoChatStore incognito,
        ILogger<ChatAccessService> logger)
    {
        _db = db;
        _workspace = workspace;
        _incognito = incognito;
        _logger = logger;
    }

    public async Task<ChatAccessKind> GetAccessAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(chatId, backfill: true, ct);
        if (owner.Deleted)
            return ChatAccessKind.Forbidden;

        if (owner.UserId is { } ownerId)
            return ownerId == userId ? ChatAccessKind.Owner : ChatAccessKind.Forbidden;

        // A live incognito thread has an owner too, just not in the database.
        if (_incognito.GetOwner(chatId) is { } incognitoOwner)
            return incognitoOwner == userId ? ChatAccessKind.Owner : ChatAccessKind.Forbidden;

        // Nothing in the database says who owns this id, yet a non-empty workspace exists on disk: a
        // legacy workspace we cannot attribute. Refuse rather than hand it to whoever asks first. An
        // empty directory holds nothing to leak, so it does not block a brand-new chat.
        var orphan = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (orphan is not null && Directory.EnumerateFileSystemEntries(orphan).Any())
        {
            _logger.LogWarning("Chat {ChatId} has a workspace but no recorded owner; access refused.", chatId);
            return ChatAccessKind.Forbidden;
        }

        return ChatAccessKind.Unclaimed;
    }

    public async Task<bool> CanReadAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(userId, chatId, ct);
        return access != ChatAccessKind.Forbidden;
    }

    public async Task EnsureWritableAsync(
        Guid userId,
        Guid chatId,
        bool incognito = false,
        string? kind = null,
        string? model = null,
        CancellationToken ct = default)
    {
        var access = await GetAccessAsync(userId, chatId, ct);
        if (access == ChatAccessKind.Forbidden)
            throw new ChatAccessDeniedException(chatId);

        if (incognito)
        {
            // INCOGNITO_CHAT: the claim lives in memory with the thread. A chat that already has a
            // database row stays owned by that row (checked above).
            if (access == ChatAccessKind.Unclaimed && !_incognito.TryClaim(chatId, userId))
                throw new ChatAccessDeniedException(chatId);
            if (access == ChatAccessKind.Owner && _incognito.GetOwner(chatId) is not null && !_incognito.TryClaim(chatId, userId))
                throw new ChatAccessDeniedException(chatId);
            return;
        }

        var row = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (row is null)
        {
            row = new ChatEntity { Id = chatId, UserId = userId, Kind = kind, Model = model };
            _db.Chats.Add(row);
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException)
            {
                // Lost a creation race for the same id: whoever won owns it.
                _db.Entry(row).State = EntityState.Detached;
                var winner = await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
                if (winner is null || winner.UserId != userId || winner.DeletedAt is not null)
                    throw new ChatAccessDeniedException(chatId);
                return;
            }
        }

        if (row.UserId != userId || row.DeletedAt is not null)
            throw new ChatAccessDeniedException(chatId);

        var changed = false;
        if (!string.IsNullOrEmpty(kind) && row.Kind != kind) { row.Kind = kind; changed = true; }
        if (!string.IsNullOrEmpty(model) && row.Model != model) { row.Model = model; changed = true; }
        if (changed)
        {
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> IsTaskOwnerAsync(Guid userId, Guid taskId, CancellationToken ct = default)
    {
        var owner = await _db.Conexy.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => (Guid?)t.UserId)
            .FirstOrDefaultAsync(ct);
        return owner == userId;
    }

    public async Task<bool> CanJoinScopeAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var taskOwner = await _db.Conexy.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => (Guid?)t.UserId)
            .FirstOrDefaultAsync(ct);
        if (taskOwner is { } ownerOfTask)
            return ownerOfTask == userId;

        // Not a task: a chat id. An unclaimed id is fine to join — nothing is broadcast to it until
        // its first run claims it, and ids are random GUIDs nobody can predict.
        return await GetAccessAsync(userId, id, ct) != ChatAccessKind.Forbidden;
    }

    public async Task MarkDeletedAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var row = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (row is null)
        {
            _db.Chats.Add(new ChatEntity { Id = chatId, UserId = userId, DeletedAt = DateTime.UtcNow });
        }
        else if (row.UserId == userId)
        {
            row.DeletedAt = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            return;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsDeletedAsync(Guid chatId, CancellationToken ct = default) =>
        await _db.Chats.AsNoTracking().AnyAsync(c => c.Id == chatId && c.DeletedAt != null, ct);

    private sealed record OwnerLookup(Guid? UserId, bool Deleted);

    /// <summary>
    /// The chat row when there is one; otherwise evidence left by chats created before ownership was
    /// recorded: their history rows, confirmation cards or a legacy task that used the chat id as its
    /// own id. The first piece of evidence found is written back as the chat row.
    /// </summary>
    private async Task<OwnerLookup> ResolveOwnerAsync(Guid chatId, bool backfill, CancellationToken ct)
    {
        var row = await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (row is not null)
            return new OwnerLookup(row.UserId, row.DeletedAt is not null);

        var legacyOwner =
            await _db.ChatMessages.AsNoTracking().Where(m => m.ChatId == chatId).Select(m => (Guid?)m.UserId).FirstOrDefaultAsync(ct)
            ?? await _db.PendingActions.AsNoTracking().Where(a => a.ChatId == chatId).Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct)
            ?? await _db.Conexy.AsNoTracking().Where(t => t.Id == chatId).Select(t => (Guid?)t.UserId).FirstOrDefaultAsync(ct);

        if (legacyOwner is null)
            return new OwnerLookup(null, false);

        if (backfill)
        {
            var backfilled = new ChatEntity { Id = chatId, UserId = legacyOwner.Value };
            try
            {
                _db.Chats.Add(backfilled);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Someone backfilled concurrently; the stored row is authoritative.
                _db.Entry(backfilled).State = EntityState.Detached;
                var stored = await _db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
                if (stored is not null)
                    return new OwnerLookup(stored.UserId, stored.DeletedAt is not null);
            }
        }

        return new OwnerLookup(legacyOwner, false);
    }
}

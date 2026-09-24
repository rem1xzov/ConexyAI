using ConexyAI.DbContext;
using Microsoft.EntityFrameworkCore;

namespace ConexyAI.Service;

// USER_DATA_CLEANUP: добавлено 2026-09-24 — ревью M9. Строки пользователя уходят каскадом по FK, а его
// файлы на диске — рабочие каталоги чатов и загруженные RAG-документы — оставались навсегда. Сервис
// вызывается ДО удаления строки пользователя: после каскада уже не узнать, какие чаты были его.
/// <summary>Deletes a user's files outside the database (chat workspaces, uploaded documents).</summary>
public interface IUserDataCleanupService
{
    /// <summary>Removes the user's workspaces and document files. Returns how many items were removed.</summary>
    Task<int> DeleteUserFilesAsync(Guid userId, CancellationToken ct = default);
}

public class UserDataCleanupService : IUserDataCleanupService
{
    private readonly DbConexy _db;
    private readonly IConexyWorkspaceService _workspace;
    private readonly ILogger<UserDataCleanupService> _logger;

    public UserDataCleanupService(DbConexy db, IConexyWorkspaceService workspace, ILogger<UserDataCleanupService> logger)
    {
        _db = db;
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<int> DeleteUserFilesAsync(Guid userId, CancellationToken ct = default)
    {
        var chatIds = new HashSet<Guid>();
        chatIds.UnionWith(await _db.Chats.AsNoTracking().Where(c => c.UserId == userId).Select(c => c.Id).ToListAsync(ct));
        chatIds.UnionWith(await _db.ChatMessages.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.ChatId).Distinct().ToListAsync(ct));
        chatIds.UnionWith(await _db.Conexy.AsNoTracking().Where(t => t.UserId == userId && t.ChatId != null).Select(t => t.ChatId!.Value).Distinct().ToListAsync(ct));

        var removed = 0;
        foreach (var chatId in chatIds)
        {
            try
            {
                await _workspace.CleanupWorkspaceAsync(chatId, ct);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete workspace of chat {ChatId} for user {UserId}.", chatId, userId);
            }
        }

        var documentPaths = await _db.Documents.AsNoTracking().Where(d => d.UserId == userId).Select(d => d.FilePath).ToListAsync(ct);
        foreach (var path in documentPaths)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete a document file of user {UserId}.", userId);
            }
        }

        _logger.LogInformation("Deleted {Count} workspace/document item(s) of user {UserId}.", removed, userId);
        return removed;
    }
}

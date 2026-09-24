namespace ConexyAI.Service;

// INCOGNITO_CHAT: добавлено 2026-09-24 — ревью M7. Инкогнито-тред жил только до простоя, но его файлы
// (вложения агентского режима) оставались на диске навсегда. Сборщик удаляет просроченные треды вместе
// с их рабочими каталогами.
/// <summary>Periodically drops idle incognito threads and deletes their workspace directories.</summary>
public class IncognitoCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly IIncognitoChatStore _store;
    private readonly IConexyWorkspaceService _workspace;
    private readonly ILogger<IncognitoCleanupService> _logger;

    public IncognitoCleanupService(
        IIncognitoChatStore store,
        IConexyWorkspaceService workspace,
        ILogger<IncognitoCleanupService> logger)
    {
        _store = store;
        _workspace = workspace;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var chatId in _store.PruneExpired())
            {
                try
                {
                    await _workspace.CleanupWorkspaceAsync(chatId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not delete the workspace of expired incognito chat {ChatId}.", chatId);
                }
            }
        }
    }
}

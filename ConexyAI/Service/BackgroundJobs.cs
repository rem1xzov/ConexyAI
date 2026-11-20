using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// BACKGROUND_JOB: добавлено 2026-11-20 (идея DeepSeek Harness: promoteOnTimeout + job_output/job_kill).
// Одноразовый bash-контейнер умирает вместе с вызовом, а долгая команда (npm install, docker build,
// большой прогон) не должна держать шаг агента и обрываться по таймауту. Такой заказ запускается
// отдельным контейнером (та же песочница: сеть/лимиты/read-only из SandboxOptions), а вывод читается
// по требованию. Это не `start_service` (dev-сервер с портом и превью), а именно фоновая задача.
public sealed record BackgroundJob(
    Guid JobId,
    Guid ChatId,
    string ContainerName,
    string Command,
    DateTime StartedAt,
    string Status,
    // Один раз прочитанный финальный вывод: контейнер после завершения удаляется, а результат нужен.
    string? FinalOutput = null);

public interface IBackgroundJobService
{
    /// <summary>Запускает команду в отдельном контейнере и возвращает её id (ответ не ждёт завершения).</summary>
    Task<(bool Started, Guid JobId, string Message)> StartAsync(Guid chatId, string command, CancellationToken ct = default);

    /// <summary>Статус + хвост вывода; завершённая задача один раз кэширует полный хвост и чистит контейнер.</summary>
    Task<string> OutputAsync(Guid chatId, Guid jobId, int lines = BackgroundJobService.DefaultLines, CancellationToken ct = default);

    /// <summary>Останавливает и удаляет контейнер задачи.</summary>
    Task<string> KillAsync(Guid chatId, Guid jobId, CancellationToken ct = default);

    /// <summary>Фоновые задачи этого чата (авторство проверяется по chatId, а не по аргументам модели).</summary>
    IReadOnlyList<BackgroundJob> List(Guid chatId);
}

public sealed class BackgroundJobService : IBackgroundJobService
{
    /// <summary>Сколько задач может идти одновременно в одном чате.</summary>
    public const int MaxJobsPerChat = 3;

    public const int DefaultLines = 80;
    public const int MaxLines = 400;

    private const int MaxCommandChars = 8_000;
    // Дольше этого задача считается брошенной: контейнер останавливается при следующем запуске.
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, BackgroundJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDockerSandboxRunner _sandbox;
    private readonly IDockerServiceRunner _services;
    private readonly IConexyWorkspaceService _workspace;
    private readonly ISandboxSessionStore _sessions;
    private readonly ILogger<BackgroundJobService> _logger;

    public BackgroundJobService(
        IDockerSandboxRunner sandbox,
        IDockerServiceRunner services,
        IConexyWorkspaceService workspace,
        ISandboxSessionStore sessions,
        ILogger<BackgroundJobService> logger)
    {
        _sandbox = sandbox;
        _services = services;
        _workspace = workspace;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<(bool Started, Guid JobId, string Message)> StartAsync(
        Guid chatId, string command, CancellationToken ct = default)
    {
        var normalized = (command ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return (false, Guid.Empty, "Фоновая задача не запущена: пустая команда.");
        if (normalized.Length > MaxCommandChars)
            return (false, Guid.Empty, $"Фоновая задача не запущена: команда длиннее {MaxCommandChars} символов.");

        await PruneExpiredAsync(chatId, ct);

        var active = _jobs.Values.Count(job => job.ChatId == chatId && job.Status == "running");
        if (active >= MaxJobsPerChat)
            return (false, Guid.Empty,
                $"В этом чате уже {active} фоновых задач (лимит {MaxJobsPerChat}). Дождись их через job_output или останови job_kill.");

        var workspacePath = _workspace.GetTaskWorkspacePathIfExists(chatId);
        if (workspacePath is null)
            return (false, Guid.Empty, "Фоновая задача не запущена: рабочая область не найдена.");
        workspacePath = _workspace.ValidateWorkspacePath(workspacePath);
        var statePath = _sessions.GetOrCreateStatePath(chatId);

        var jobId = Guid.NewGuid();
        var containerName = $"conexy-job-{chatId:N}-{jobId.ToString("N")[..8]}";
        var (ok, output) = await _sandbox.StartBackgroundAsync(normalized, workspacePath, statePath, containerName, ct);
        if (!ok)
            return (false, Guid.Empty, $"Фоновая задача не запущена: {output}");

        var job = new BackgroundJob(jobId, chatId, containerName, normalized, DateTime.UtcNow, "running");
        _jobs[Key(chatId, jobId)] = job;
        _sessions.Touch(chatId);
        _logger.LogInformation(
            "Background job {JobId} started for chat {ChatId} (container {Container}, {Chars} chars).",
            jobId, chatId, containerName, normalized.Length);

        return (true, jobId,
            $"Запущено в фоне: job_id={jobId}. Вывод: job_output(job_id=\"{jobId}\"), остановить: job_kill(job_id=\"{jobId}\"). " +
            "Работай дальше параллельно и не повторяй эту команду: результат придёт через job_output.");
    }

    public async Task<string> OutputAsync(
        Guid chatId, Guid jobId, int lines = DefaultLines, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(Key(chatId, jobId), out var job))
            return "Фоновая задача не найдена (возможно, она принадлежит другому чату).";

        lines = Math.Clamp(lines <= 0 ? DefaultLines : lines, 1, MaxLines);
        var running = await _services.IsRunningAsync(job.ContainerName, ct);
        var logs = running ? await _services.TailLogsAsync(job.ContainerName, lines, ct) : string.Empty;

        if (!running && job.Status == "running")
        {
            // Финальный вывод читаем ДО удаления контейнера: после docker rm логи недоступны.
            var finalLogs = await _services.TailLogsAsync(job.ContainerName, MaxLines, ct);
            job = job with { Status = "finished", FinalOutput = finalLogs };
            _jobs[Key(chatId, jobId)] = job;
            await _services.StopServiceAsync(job.ContainerName);
            _logger.LogInformation("Background job {JobId} finished for chat {ChatId}.", jobId, chatId);
        }

        var body = job.FinalOutput ?? logs;
        if (job.Status == "killed" && string.IsNullOrWhiteSpace(body))
            body = "(задача была остановлена, вывод после остановки недоступен)";
        if (string.IsNullOrWhiteSpace(body))
            body = "(вывода пока нет)";

        return $"Задача {Short(job.JobId)}: {StatusLabel(job.Status)}, запущена {job.StartedAt:HH:mm:ss}, "
               + $"команда: {Truncate(job.Command, 160)}.\n{body}";
    }

    public async Task<string> KillAsync(Guid chatId, Guid jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(Key(chatId, jobId), out var job))
            return "Фоновая задача не найдена (возможно, она принадлежит другому чату).";
        if (job.Status != "running")
            return $"Задача {Short(job.JobId)} уже завершена ({StatusLabel(job.Status)}).";

        await _services.StopServiceAsync(job.ContainerName);
        _jobs[Key(chatId, jobId)] = job with { Status = "killed" };
        _logger.LogInformation("Background job {JobId} killed for chat {ChatId}.", jobId, chatId);
        return $"Задача {Short(job.JobId)} остановлена.";
    }

    public IReadOnlyList<BackgroundJob> List(Guid chatId) =>
        _jobs.Values
            .Where(job => job.ChatId == chatId)
            .OrderByDescending(job => job.StartedAt)
            .ToList();

    /// <summary>Останавливает задачи, пережившие <see cref="MaxLifetime"/>: контейнер не должен жить вечно.</summary>
    private async Task PruneExpiredAsync(Guid chatId, CancellationToken ct)
    {
        foreach (var job in _jobs.Values.Where(job => job.ChatId == chatId && job.Status == "running"))
        {
            if (DateTime.UtcNow - job.StartedAt < MaxLifetime)
                continue;
            try
            {
                await _services.StopServiceAsync(job.ContainerName);
                _jobs[Key(chatId, job.JobId)] = job with { Status = "killed" };
                _logger.LogWarning("Background job {JobId} exceeded {Hours}h and was stopped.", job.JobId, MaxLifetime.TotalHours);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop expired background job {JobId}.", job.JobId);
            }
        }
    }

    private static string Key(Guid chatId, Guid jobId) => $"{chatId:N}:{jobId:N}";

    private static string Short(Guid jobId) => jobId.ToString()[..8];

    private static string StatusLabel(string status) => status switch
    {
        "running" => "выполняется",
        "finished" => "завершена",
        "killed" => "остановлена",
        _ => status,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

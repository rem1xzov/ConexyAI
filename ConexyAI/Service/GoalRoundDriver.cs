using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// GOAL: добавлено 2026-11-20 (идея DeepSeek Harness, packages/goal + goal-round-driver) — раунд-драйвер
// долгоживущих целей. Каждые N секунд берёт активные цели, у которых не исчерпан лимит, и запускает
// следующий раунд обычным ходом агента: чат при этом простаивает (IConexyQueueGuard не даёт двум ходам
// идти параллельно). Раннер получает цель в контексте, агент закрывает её goal_complete/goal_block.
public sealed class GoalRoundDriver : BackgroundService
{
    /// <summary>Как часто проверять готовые к раунду цели.</summary>
    public const int PollSeconds = 15;

    /// <summary>Сколько раундов запускать за один проход (остальные — следующим).</summary>
    public const int MaxRoundsPerPass = 2;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConexyQueueGuard _queue;
    private readonly ILogger<GoalRoundDriver> _logger;

    public GoalRoundDriver(
        IServiceScopeFactory scopeFactory,
        IConexyQueueGuard queue,
        ILogger<GoalRoundDriver> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Даём воркеру и миграциям встать, чтобы первый проход не упал на неготовой схеме.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Goal round driver pass failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Один проход драйвера; внутренний, чтобы тест вызывал его напрямую.</summary>
    internal async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var goals = scope.ServiceProvider.GetRequiredService<IGoalService>();
        var repository = scope.ServiceProvider.GetRequiredService<IConexyRepository>();

        var runnable = await goals.ListRunnableAsync(ct);
        var started = 0;
        // Копия на случай, если реализация вернёт живую коллекцию: раунд записи меняет состояние цели.
        foreach (var goal in runnable.ToList())
        {
            if (started >= MaxRoundsPerPass)
                break;

            var taskId = Guid.NewGuid();
            // Чат занят своим ходом — раунд подождёт: два хода в одном чате не запускаются.
            if (!_queue.TryReserve(taskId, goal.ChatId))
                continue;

            try
            {
                var model = await goals.ResolveRoundModelAsync(goal.ChatId, ct);
                var round = goal.RoundsStarted + 1;
                var prompt = RoundPrompt(goal, round);
                var entity = await repository.CreateOrGetAsync(new ConexyEntity
                {
                    Id = taskId,
                    UserId = goal.UserId,
                    ChatId = goal.ChatId,
                    Model = model.ToPublicName(),
                    Prompt = prompt,
                    Status = ConexyStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                }, ct);
                if (entity.Status is ConexyStatus.Completed or ConexyStatus.Cancelled)
                {
                    // Строка с таким id уже закрыта — очередь бы её молча проигнорировала.
                    _queue.MarkCompleted(taskId);
                    continue;
                }

                // Счётчик раундов увеличиваем ДО постановки в очередь: даже если прогон упадёт,
                // цель не будет пытаться бесконечно повторять один и тот же раунд.
                var recorded = await goals.RecordRoundAsync(goal.Id, taskId, note: null, ct);
                if (recorded is null || recorded.Status != GoalStatuses.Active)
                {
                    _queue.MarkCompleted(taskId);
                    continue;
                }

                var job = new ConexyJob(
                    TaskId: taskId,
                    ChatId: goal.ChatId,
                    UserId: goal.UserId,
                    ModelType: model,
                    Prompt: prompt,
                    GoalId: goal.Id,
                    GoalRound: round);
                await _queue.EnqueueReservedAsync(job, ct);
                started++;
                _logger.LogInformation(
                    "Goal {GoalId}: round {Round}/{Max} started (task {TaskId}).",
                    goal.Id, round, goal.MaxRounds, taskId);
            }
            catch (Exception ex)
            {
                _queue.MarkCompleted(taskId);
                _logger.LogWarning(ex, "Goal {GoalId}: failed to start a round.", goal.Id);
            }
        }

        return started;
    }

    internal static string RoundPrompt(GoalDto goal, int round) =>
        $"Продолжай активную цель (раунд {round} из {goal.MaxRounds}). " +
        "Работай над ней в этом ходе до проверенного результата, не начинай посторонние задачи; " +
        "когда цель достигнута — вызови `goal_complete`, при внешней блокировке — `goal_block`.";
}

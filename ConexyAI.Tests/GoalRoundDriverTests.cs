using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// GOAL: добавлено 2026-11-20
/// <summary>
/// Раунд-драйвер целей: сам запускает следующий раунд обычным ходом агента, не влезает в занятый чат,
/// ограничивает число раундов за проход и освобождает резерв очереди, если запуск не удался.
/// </summary>
internal static class GoalRoundDriverTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("goal driver: an active goal starts its next round as a normal agent turn", StartsRoundAsync);
        TestRegistry.Add("goal driver: a busy chat is skipped until its current turn is over", BusyChatAsync);
        TestRegistry.Add("goal driver: no more than the per-pass limit is started", PerPassLimitAsync);
        TestRegistry.Add("goal driver: a failed round start releases the queue reservation", ReleaseAsync);
        TestRegistry.Add("goal driver: the round prompt points at the mark and the closing tools", PromptAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static GoalDto Goal(int rounds = 0, int max = 5, string objective = "Сделать релиз") => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        objective,
        GoalStatuses.Active,
        rounds,
        max,
        null,
        null,
        DateTime.UtcNow,
        DateTime.UtcNow);

    private static (GoalRoundDriver Driver, FakeGoals Goals, FakeQueue Queue, FakeRepository Repository) Build(
        IEnumerable<GoalDto> runnable)
    {
        var goals = new FakeGoals(runnable.ToList());
        var queue = new FakeQueue();
        var repository = new FakeRepository();
        var services = new ServiceCollection();
        services.AddScoped<IGoalService>(_ => goals);
        services.AddScoped<IConexyRepository>(_ => repository);
        var provider = services.BuildServiceProvider();
        var driver = new GoalRoundDriver(
            provider.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger<GoalRoundDriver>.Instance);
        return (driver, goals, queue, repository);
    }

    private static async Task StartsRoundAsync()
    {
        var goal = Goal();
        var (driver, goals, queue, repository) = Build([goal]);

        var started = await driver.TickAsync(CancellationToken.None);

        Assert(started == 1, $"one round starts, got {started}");
        Assert(queue.Jobs.Count == 1, "one job is enqueued");
        var job = queue.Jobs[0];
        Assert(job.GoalId == goal.Id, "the job carries the goal id");
        Assert(job.GoalRound == 1, $"the first round is number 1, got {job.GoalRound}");
        Assert(job.ChatId == goal.ChatId && job.UserId == goal.UserId, "the round runs in the goal's chat as its owner");
        Assert(job.ModelType == ConexyModelType.ConexyCoder, "the round defaults to the coder model");
        Assert(goals.Recorded.Count == 1 && goals.Recorded[0] == goal.Id, "the round counter is recorded before enqueueing");
        Assert(repository.Created.Count == 1 && repository.Created[0].Id == job.TaskId,
            "the task row is created before the job is queued (the worker drops unknown jobs)");
        Assert(repository.Created[0].Status == ConexyStatus.Pending, "the task starts as pending");
    }

    private static async Task BusyChatAsync()
    {
        var (driver, _, queue, _) = Build([Goal()]);
        FakeQueue.Busy = true;
        try
        {
            var started = await driver.TickAsync(CancellationToken.None);
            Assert(started == 0 && queue.Jobs.Count == 0, "a chat with a turn in flight is not started twice");
        }
        finally
        {
            FakeQueue.Busy = false;
        }
    }

    private static async Task PerPassLimitAsync()
    {
        var goals = Enumerable.Range(0, GoalRoundDriver.MaxRoundsPerPass + 1).Select(_ => Goal()).ToList();
        var (driver, _, queue, _) = Build(goals);

        var started = await driver.TickAsync(CancellationToken.None);

        Assert(started == GoalRoundDriver.MaxRoundsPerPass,
            $"at most {GoalRoundDriver.MaxRoundsPerPass} rounds start per pass, got {started}");
        Assert(queue.Jobs.Count == GoalRoundDriver.MaxRoundsPerPass, "the rest wait for the next pass");
    }

    private static async Task ReleaseAsync()
    {
        var goal = Goal();
        var (driver, goals, queue, _) = Build([goal]);
        goals.FailRecording = true;

        var started = await driver.TickAsync(CancellationToken.None);

        Assert(started == 0 && queue.Jobs.Count == 0, "a failed round start enqueues nothing");
        Assert(queue.Released.Count == 1, "the queue reservation is released so the goal can retry later");
    }

    private static Task PromptAsync()
    {
        var goal = Goal(rounds: 2, max: 6) with { LastNote = "сборка зелёная, осталось выложить" };
        var text = ConexyAgentRunner.GoalPrompt(goal, round: 3);

        Assert(text.Contains("Раунд 3 из 6", StringComparison.Ordinal), "the round number and limit are named: " + text);
        Assert(text.Contains(goal.Objective, StringComparison.Ordinal), "the objective is present");
        Assert(text.Contains("сборка зелёная", StringComparison.Ordinal), "the previous round note is present");
        Assert(text.Contains("goal_complete", StringComparison.Ordinal) && text.Contains("goal_block", StringComparison.Ordinal),
            "the closing tools are named");
        return Task.CompletedTask;
    }

    private sealed class FakeGoals(List<GoalDto> runnable) : IGoalService
    {
        public List<Guid> Recorded { get; } = new();
        public bool FailRecording { get; set; }

        public Task<IReadOnlyList<GoalDto>> ListRunnableAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GoalDto>>(runnable);

        public Task<ConexyModelType> ResolveRoundModelAsync(Guid chatId, CancellationToken ct = default) =>
            Task.FromResult(ConexyModelType.ConexyCoder);

        public Task<GoalDto?> RecordRoundAsync(Guid goalId, Guid taskId, string? note, CancellationToken ct = default)
        {
            if (FailRecording)
                throw new InvalidOperationException("database is down");
            Recorded.Add(goalId);
            var goal = runnable.First(g => g.Id == goalId);
            return Task.FromResult<GoalDto?>(goal with { RoundsStarted = goal.RoundsStarted + 1 });
        }

        public Task<GoalDto?> GetActiveAsync(Guid userId, Guid chatId, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);

        public Task<GoalDto?> GetAsync(Guid userId, Guid goalId, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);

        public Task<GoalDto?> StartAsync(Guid userId, Guid chatId, string? objective, int? maxRounds, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);

        public Task<GoalDto?> SetStatusAsync(Guid userId, Guid chatId, string? status, string? reason, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);

        public Task<bool> DeleteAsync(Guid userId, Guid chatId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<GoalDto?> FinalizeRoundAsync(Guid goalId, string? note, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);

        public Task<GoalDto?> CompleteAsync(Guid goalId, string? summary, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);

        public Task<GoalDto?> BlockAsync(Guid goalId, string? reason, CancellationToken ct = default) =>
            Task.FromResult<GoalDto?>(null);
    }

    private sealed class FakeQueue : IConexyQueueGuard
    {
        public static bool Busy { get; set; }
        public List<ConexyJob> Jobs { get; } = new();
        public List<Guid> Released { get; } = new();

        public bool TryReserve(Guid taskId) => !Busy;

        public bool TryReserve(Guid taskId, Guid chatId) => !Busy;

        public Task EnqueueReservedAsync(ConexyJob job, CancellationToken ct = default)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }

        public void MarkCompleted(Guid taskId) => Released.Add(taskId);

        public Task<bool> EnqueueIfNotInFlightAsync(ConexyJob job, CancellationToken ct = default) =>
            Task.FromResult(false);

        public bool IsInFlight(Guid taskId) => false;
    }

    private sealed class FakeRepository : IConexyRepository
    {
        public List<ConexyEntity> Created { get; } = new();

        public Task<ConexyEntity> CreateOrGetAsync(ConexyEntity entity, CancellationToken ct = default)
        {
            Created.Add(entity);
            return Task.FromResult(entity);
        }

        public Task UpdateAsync(ConexyEntity entity, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<ConexyEntity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ConexyEntity?>(null);
        public Task<ConexyEntity?> GetByIdAsNoTrackingAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ConexyEntity?>(null);
        public Task<IEnumerable<ConexyEntity>> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<ConexyEntity>>([]);
        public Task<int> GetRequestCountInWindowAsync(Guid userId, DateTime since, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Guid>> FailUnfinishedAsync(string reason, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}

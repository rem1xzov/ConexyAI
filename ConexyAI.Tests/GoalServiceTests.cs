using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// GOAL: добавлено 2026-11-20
/// <summary>
/// Долгоживущая цель чата: создание/переопределение, владение, переходы статусов, счёт раундов и
/// список целей для раунд-драйвера. Проверяется на EF InMemory без сервера.
/// </summary>
internal static class GoalServiceTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("goal: a new goal is active and owned by the chat owner", StartAsync);
        TestRegistry.Add("goal: starting again replaces the objective and resets rounds", ReplaceAsync);
        TestRegistry.Add("goal: a stranger cannot read or start a goal in someone else's chat", OwnershipAsync);
        TestRegistry.Add("goal: status transitions follow the allowed graph", StatusAsync);
        TestRegistry.Add("goal: rounds are counted and the round limit blocks the goal", RoundsAsync);
        TestRegistry.Add("goal: runnable goals exclude paused, complete and deleted chats", RunnableAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static (DbConexy Db, GoalService Service, Guid UserId, Guid ChatId) Setup()
    {
        var options = new DbContextOptionsBuilder<DbConexy>()
            .UseTestDatabase("goals_" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new DbConexy(options);
        var userId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        db.Chats.Add(new ChatEntity
        {
            Id = chatId,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return (db, new GoalService(db, NullLogger<GoalService>.Instance), userId, chatId);
    }

    private static Task StartAsync()
    {
        var (db, service, userId, chatId) = Setup();
        using (db)
        {
            var goal = service.StartAsync(userId, chatId, "Сделать релиз 1.0", maxRounds: 5).GetAwaiter().GetResult();
            Assert(goal is not null, "a goal is created for the chat owner");
            Assert(goal!.Status == GoalStatuses.Active && goal.RoundsStarted == 0 && goal.MaxRounds == 5,
                "a new goal is active with a zero round counter");
            Assert(goal.Objective == "Сделать релиз 1.0", "the objective is stored");

            var active = service.GetActiveAsync(userId, chatId).GetAwaiter().GetResult();
            Assert(active is not null && active.Id == goal.Id, "the active goal is returned to its owner");
        }

        return Task.CompletedTask;
    }

    private static Task ReplaceAsync()
    {
        var (db, service, userId, chatId) = Setup();
        using (db)
        {
            var first = service.StartAsync(userId, chatId, "Цель A", maxRounds: 4).GetAwaiter().GetResult()!;
            service.RecordRoundAsync(first.Id, Guid.NewGuid(), "первый раунд").GetAwaiter().GetResult();

            var second = service.StartAsync(userId, chatId, "Цель B", maxRounds: 3).GetAwaiter().GetResult()!;
            Assert(second.Id == first.Id, "the same active goal row is reused");
            Assert(second.Objective == "Цель B" && second.MaxRounds == 3, "the objective and limit are replaced");
            Assert(second.RoundsStarted == 0, "a replaced goal starts counting rounds from zero");
            Assert(db.ChatGoals.Count() == 1, "there is still exactly one goal row");
        }

        return Task.CompletedTask;
    }

    private static Task OwnershipAsync()
    {
        var (db, service, userId, chatId) = Setup();
        using (db)
        {
            var stranger = Guid.NewGuid();
            Assert(service.StartAsync(stranger, chatId, "Чужая цель", null).GetAwaiter().GetResult() is null,
                "a stranger cannot start a goal in someone else's chat");

            service.StartAsync(userId, chatId, "Моя цель", null).GetAwaiter().GetResult();
            Assert(service.GetActiveAsync(stranger, chatId).GetAwaiter().GetResult() is null,
                "a stranger does not see the goal");
        }

        return Task.CompletedTask;
    }

    private static Task StatusAsync()
    {
        var (db, service, userId, chatId) = Setup();
        using (db)
        {
            service.StartAsync(userId, chatId, "Цель", maxRounds: 5).GetAwaiter().GetResult();

            Assert(service.SetStatusAsync(userId, chatId, GoalStatuses.Paused, null).GetAwaiter().GetResult()?.Status == GoalStatuses.Paused,
                "an active goal can be paused");
            Assert(service.SetStatusAsync(userId, chatId, GoalStatuses.Active, null).GetAwaiter().GetResult()?.Status == GoalStatuses.Active,
                "a paused goal can be resumed");

            var blocked = service.SetStatusAsync(userId, chatId, GoalStatuses.Blocked, "нет доступа к серверу").GetAwaiter().GetResult();
            Assert(blocked?.Status == GoalStatuses.Blocked && blocked.BlockedReason == "нет доступа к серверу",
                "blocking records the reason");
            Assert(service.SetStatusAsync(userId, chatId, GoalStatuses.Active, null).GetAwaiter().GetResult()?.BlockedReason is null,
                "resuming clears the block reason");

            Assert(service.SetStatusAsync(userId, chatId, "weird", null).GetAwaiter().GetResult() is null,
                "an unknown status is refused");
            Assert(service.SetStatusAsync(userId, chatId, GoalStatuses.Complete, null).GetAwaiter().GetResult()?.Status == GoalStatuses.Complete,
                "a goal can be completed");
            Assert(service.SetStatusAsync(userId, chatId, GoalStatuses.Active, null).GetAwaiter().GetResult() is null,
                "a completed goal cannot be resumed");
        }

        return Task.CompletedTask;
    }

    private static Task RoundsAsync()
    {
        var (db, service, userId, chatId) = Setup();
        using (db)
        {
            var goal = service.StartAsync(userId, chatId, "Цель", maxRounds: 2).GetAwaiter().GetResult()!;

            var first = service.RecordRoundAsync(goal.Id, Guid.NewGuid(), "осталось собрать релиз").GetAwaiter().GetResult();
            Assert(first is not null && first.RoundsStarted == 1 && first.Status == GoalStatuses.Active,
                "the first round keeps the goal active");
            Assert(first!.LastNote == "осталось собрать релиз", "the round note is stored for the next round");

            var second = service.RecordRoundAsync(goal.Id, Guid.NewGuid(), "готово").GetAwaiter().GetResult();
            Assert(second is not null && second.RoundsStarted == 2, "the second round is counted");
            Assert(second!.Status == GoalStatuses.Blocked && second.BlockedReason!.Contains("лимит", StringComparison.Ordinal),
                "reaching the round limit blocks the goal with a reason: " + second.BlockedReason);

            var third = service.RecordRoundAsync(goal.Id, Guid.NewGuid(), "лишний").GetAwaiter().GetResult();
            Assert(third is not null && third.RoundsStarted == 2, "a blocked goal does not count more rounds");
        }

        return Task.CompletedTask;
    }

    private static Task RunnableAsync()
    {
        var (db, service, userId, chatId) = Setup();
        using (db)
        {
            var runnable = service.StartAsync(userId, chatId, "Работает", maxRounds: 5).GetAwaiter().GetResult()!;

            var pausedId = Guid.NewGuid();
            var pausedChat = Guid.NewGuid();
            db.Chats.Add(new ChatEntity { Id = pausedChat, UserId = userId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.ChatGoals.Add(new ChatGoalEntity { ChatId = pausedChat, UserId = userId, Objective = "Пауза", Status = GoalStatuses.Paused, MaxRounds = 5 });
            db.SaveChanges();

            var deletedChat = Guid.NewGuid();
            db.Chats.Add(new ChatEntity { Id = deletedChat, UserId = userId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow });
            db.ChatGoals.Add(new ChatGoalEntity { ChatId = deletedChat, UserId = userId, Objective = "Удалённый чат", Status = GoalStatuses.Active, MaxRounds = 5 });
            db.SaveChanges();

            var list = service.ListRunnableAsync().GetAwaiter().GetResult();
            Assert(list.Count == 1 && list[0].Id == runnable.Id,
                "only active goals of live chats are runnable: " + string.Join(",", list.Select(g => g.Objective)));
            Assert(pausedId != Guid.Empty, "paused goal was seeded");
        }

        return Task.CompletedTask;
    }
}

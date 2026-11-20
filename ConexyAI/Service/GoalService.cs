using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConexyAI.Service;

// GOAL: добавлено 2026-11-20 (идея DeepSeek Harness, packages/goal) — долгоживущая цель чата.
// API начинает/ставит на паузу/завершает цель, раунд-драйвер запускает продолжения, раннер получает
// цель в контексте. Один активный раунд на чат гарантирует IConexyQueueGuard; здесь — только состояние.
public interface IGoalService
{
    /// <summary>Активная цель чата, если пользователь владеет чатом.</summary>
    Task<GoalDto?> GetActiveAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>Цель по id (только владельцу).</summary>
    Task<GoalDto?> GetAsync(Guid userId, Guid goalId, CancellationToken ct = default);

    /// <summary>Ставит новую цель или переопределяет активную (сбрасывает раунды). Null — чат не принадлежит пользователю/пустое задание.</summary>
    Task<GoalDto?> StartAsync(Guid userId, Guid chatId, string? objective, int? maxRounds, CancellationToken ct = default);

    /// <summary>active | paused | blocked | complete. Null — недопустимый переход или чужая цель.</summary>
    Task<GoalDto?> SetStatusAsync(Guid userId, Guid chatId, string? status, string? reason, CancellationToken ct = default);

    /// <summary>Удаляет цели чата пользователя (отмена цели).</summary>
    Task<bool> DeleteAsync(Guid userId, Guid chatId, CancellationToken ct = default);

    /// <summary>Цели, готовые к следующему раунду (активны, лимит не исчерпан, чат не удалён). Для драйвера.</summary>
    Task<IReadOnlyList<GoalDto>> ListRunnableAsync(CancellationToken ct = default);

    /// <summary>Учитывает запущенный раунд; на последнем раунде переводит цель в blocked с причиной.</summary>
    Task<GoalDto?> RecordRoundAsync(Guid goalId, Guid taskId, string? note, CancellationToken ct = default);
}

public sealed class GoalService : IGoalService
{
    public const int DefaultMaxRounds = 10;
    public const int HardMaxRounds = 50;
    public const int MaxObjectiveChars = 4_000;
    private const int MaxNoteChars = 2_000;

    // Раунд-драйвер один на процесс, но запись цели должна быть последовательной и в тестах.
    private static readonly SemaphoreSlim RoundGate = new(1, 1);

    private readonly DbConexy _db;
    private readonly ILogger<GoalService> _logger;

    public GoalService(DbConexy db, ILogger<GoalService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GoalDto?> GetActiveAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var goal = await _db.ChatGoals.AsNoTracking()
            .Where(g => g.ChatId == chatId && g.UserId == userId && g.Status == GoalStatuses.Active)
            .OrderByDescending(g => g.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        return goal is null ? null : ToDto(goal);
    }

    public async Task<GoalDto?> GetAsync(Guid userId, Guid goalId, CancellationToken ct = default)
    {
        var goal = await _db.ChatGoals.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == goalId && g.UserId == userId, ct);
        return goal is null ? null : ToDto(goal);
    }

    public async Task<GoalDto?> StartAsync(Guid userId, Guid chatId, string? objective, int? maxRounds, CancellationToken ct = default)
    {
        var text = (objective ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxObjectiveChars)
            return null;

        var ownsChat = await _db.Chats.AnyAsync(
            c => c.Id == chatId && c.UserId == userId && c.DeletedAt == null, ct);
        if (!ownsChat)
            return null;

        var rounds = Math.Clamp(maxRounds ?? DefaultMaxRounds, 1, HardMaxRounds);
        var now = DateTime.UtcNow;
        var goal = await _db.ChatGoals
            .Where(g => g.ChatId == chatId && g.Status == GoalStatuses.Active)
            .OrderByDescending(g => g.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (goal is null)
        {
            goal = new ChatGoalEntity
            {
                ChatId = chatId,
                UserId = userId,
                CreatedAt = now,
            };
            _db.ChatGoals.Add(goal);
        }

        // Переопределение активной цели: новое задание начинает отсчёт раундов заново.
        goal.Objective = text;
        goal.MaxRounds = rounds;
        goal.RoundsStarted = 0;
        goal.Status = GoalStatuses.Active;
        goal.BlockedReason = null;
        goal.LastNote = null;
        goal.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Goal {GoalId} started for chat {ChatId} (max {Rounds} rounds).", goal.Id, chatId, rounds);
        return ToDto(goal);
    }

    public async Task<GoalDto?> SetStatusAsync(Guid userId, Guid chatId, string? status, string? reason, CancellationToken ct = default)
    {
        if (!GoalStatuses.IsValid(status))
            return null;

        var goal = await _db.ChatGoals
            .Where(g => g.ChatId == chatId && g.UserId == userId)
            .OrderByDescending(g => g.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (goal is null || goal.Status == GoalStatuses.Complete)
            return null;

        var allowed = status switch
        {
            GoalStatuses.Active => goal.Status is GoalStatuses.Paused or GoalStatuses.Blocked,
            GoalStatuses.Paused => goal.Status == GoalStatuses.Active,
            GoalStatuses.Blocked => goal.Status is GoalStatuses.Active or GoalStatuses.Paused,
            GoalStatuses.Complete => true,
            _ => false,
        };
        if (!allowed)
            return null;

        goal.Status = status;
        goal.BlockedReason = status == GoalStatuses.Blocked
            ? Truncate(reason, 600) ?? "остановлено пользователем"
            : null;
        goal.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Goal {GoalId} switched to {Status}.", goal.Id, status);
        return ToDto(goal);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var goals = await _db.ChatGoals.Where(g => g.ChatId == chatId && g.UserId == userId).ToListAsync(ct);
        if (goals.Count == 0)
            return false;
        _db.ChatGoals.RemoveRange(goals);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<GoalDto>> ListRunnableAsync(CancellationToken ct = default)
    {
        var goals = await _db.ChatGoals.AsNoTracking()
            .Where(g => g.Status == GoalStatuses.Active && g.RoundsStarted < g.MaxRounds)
            .Where(g => _db.Chats.Any(c => c.Id == g.ChatId && c.DeletedAt == null))
            .OrderBy(g => g.UpdatedAt)
            .Take(100)
            .ToListAsync(ct);
        return goals.Select(ToDto).ToList();
    }

    public async Task<GoalDto?> RecordRoundAsync(Guid goalId, Guid taskId, string? note, CancellationToken ct = default)
    {
        await RoundGate.WaitAsync(ct);
        try
        {
            var goal = await _db.ChatGoals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
            if (goal is null)
                return null;
            if (goal.Status != GoalStatuses.Active)
                return ToDto(goal);

            goal.RoundsStarted++;
            goal.LastTaskId = taskId;
            goal.LastNote = Truncate(note, MaxNoteChars);
            goal.UpdatedAt = DateTime.UtcNow;
            if (goal.RoundsStarted >= goal.MaxRounds)
            {
                goal.Status = GoalStatuses.Blocked;
                goal.BlockedReason = $"исчерпан лимит раундов ({goal.MaxRounds})";
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Goal {GoalId} round recorded ({Rounds}/{Max}, status {Status}).", goal.Id, goal.RoundsStarted, goal.MaxRounds, goal.Status);
            return ToDto(goal);
        }
        finally
        {
            RoundGate.Release();
        }
    }

    private static GoalDto ToDto(ChatGoalEntity goal) => new(
        goal.Id,
        goal.ChatId,
        goal.Objective,
        goal.Status,
        goal.RoundsStarted,
        goal.MaxRounds,
        goal.BlockedReason,
        goal.LastNote,
        goal.CreatedAt,
        goal.UpdatedAt);

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}

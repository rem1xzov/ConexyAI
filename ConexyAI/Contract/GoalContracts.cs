namespace ConexyAI.Contract;

// GOAL: добавлено 2026-11-20 — контракты долгоживущей цели (API + раунд-драйвер + раннер).
/// <summary>Valid status values of a chat goal.</summary>
public static class GoalStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Blocked = "blocked";
    public const string Complete = "complete";

    public static bool IsValid(string? status) =>
        status is Active or Paused or Blocked or Complete;
}

/// <summary>Состояние цели чата, как его видят API, драйвер и агент.</summary>
public sealed record GoalDto(
    Guid Id,
    Guid ChatId,
    string Objective,
    string Status,
    int RoundsStarted,
    int MaxRounds,
    string? BlockedReason,
    string? LastNote,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Тело запроса создания цели.</summary>
public sealed record CreateGoalRequest(string? Objective, int? MaxRounds);

/// <summary>Тело запроса изменения состояния цели.</summary>
public sealed record UpdateGoalRequest(string? Status, string? Reason);

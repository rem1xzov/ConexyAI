namespace ConexyAI.Entity;

// GOAL: добавлено 2026-11-20 (идея DeepSeek Harness, packages/goal) — долгоживущая цель чата.
// Обычный ход агента заканчивается ответом, а цель живёт между ходами: раунд-драйвер запускает
// следующие раунды, пока цель активна и не исчерпан лимит. Владелец — пользователь чата; строка
// переживает рестарт сервера, поэтому цель видна и после перезапуска, и её можно поставить на паузу.
public class ChatGoalEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChatId { get; set; }

    /// <summary>Владелец цели (тот же, кто владеет чатом).</summary>
    public Guid UserId { get; set; }

    /// <summary>Что нужно довести до конца; текст видит модель в каждом раунде.</summary>
    public string Objective { get; set; } = string.Empty;

    /// <summary>active | paused | blocked | complete.</summary>
    public string Status { get; set; } = Contract.GoalStatuses.Active;

    /// <summary>Сколько раундов уже запущено/выполнено.</summary>
    public int RoundsStarted { get; set; }

    /// <summary>Сколько раундов разрешено всего.</summary>
    public int MaxRounds { get; set; } = 10;

    /// <summary>Почему цель заблокирована (внешняя причина, лимит раундов и т.п.).</summary>
    public string? BlockedReason { get; set; }

    /// <summary>Короткая заметка последнего раунда (итог/что осталось) — её видит следующий раунд.</summary>
    public string? LastNote { get; set; }

    /// <summary>Задача последнего раунда: по ней видно, что раунд действительно запускался.</summary>
    public Guid? LastTaskId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

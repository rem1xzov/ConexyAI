namespace ConexyAI.Contract;

public record ConexyResponse(
    Guid Id,
    Guid UserId,
    string Model,
    string Prompt,
    string? Result,
    string Status,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    // WEB_DEVICE: добавлено 2026-10-18 — задача ждёт подключения ПК (политика "queue").
    bool WaitingForDevice = false
);
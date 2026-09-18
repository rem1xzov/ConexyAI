namespace ConexyAI.Contract;

public record ConexyResponse(
    Guid Id,
    Guid UserId,
    string Model,
    string Prompt,
    string? Result,
    string Status,
    DateTime CreatedAt,
    DateTime? FinishedAt
);
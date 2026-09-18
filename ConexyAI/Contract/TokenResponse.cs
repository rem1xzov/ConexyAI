namespace ConexyAI.Contract;

public record TokenResponse(
    string Token,
    DateTime ExpiresAtUtc,
    int LifetimeMinutes
);

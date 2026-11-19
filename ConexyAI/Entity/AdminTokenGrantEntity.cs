namespace ConexyAI.Entity;

// ADMIN_TOKEN_CREDIT: добавлено 2026-11-19 — админ начисляет токены пользователю вручную.
// Таблица — журнал: кто из админов, когда, в какой пул и сколько начислил (плюс причина).
/// <summary>Audit row for one manual admin token credit.</summary>
public class AdminTokenGrantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User whose pool was credited.</summary>
    public Guid UserId { get; set; }

    /// <summary>Admin who performed the credit.</summary>
    public Guid AdminId { get; set; }

    /// <summary>"Coder" or "Cowork".</summary>
    public string Pool { get; set; } = TokenPools.Coder;

    public long Amount { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Pool names used by the token top-up fields on <see cref="User"/>.</summary>
public static class TokenPools
{
    public const string Coder = "Coder";
    public const string Cowork = "Cowork";

    /// <summary>Normalizes a client-supplied pool name; null when it is not a known pool.</summary>
    public static string? Normalize(string? pool)
    {
        if (string.IsNullOrWhiteSpace(pool)) return null;
        if (string.Equals(pool, Coder, StringComparison.OrdinalIgnoreCase)) return Coder;
        if (string.Equals(pool, Cowork, StringComparison.OrdinalIgnoreCase)) return Cowork;
        return null;
    }
}

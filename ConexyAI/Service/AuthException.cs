namespace ConexyAI.Service;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>
/// A structured authentication error with a stable machine-readable <see cref="Code"/> and a
/// human-readable message. The auth controller maps it to a JSON <c>{ code, message }</c>
/// response so the frontend can render it inline in the form.
/// </summary>
public class AuthException : Exception
{
    // LOGIN_LOCKOUT: добавлено 2026-09-24 (ревью M20)
    /// <summary>Code of the lockout error; the controller answers it with HTTP 429.</summary>
    public const string TooManyAttemptsCode = "too_many_attempts";

    public string Code { get; }
    public int StatusCode { get; }

    /// <summary>Seconds the client should wait before retrying (lockout only), else <c>null</c>.</summary>
    public int? RetryAfterSeconds { get; }

    public AuthException(string code, string message, int statusCode = 400, int? retryAfterSeconds = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>The per-account lockout error (HTTP 429).</summary>
    public static AuthException TooManyAttempts(TimeSpan retryAfter) => new(
        TooManyAttemptsCode,
        "Слишком много попыток входа. Попробуйте позже.",
        StatusCodes.Status429TooManyRequests,
        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
}

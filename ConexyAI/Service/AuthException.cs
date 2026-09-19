namespace ConexyAI.Service;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>
/// A structured authentication error with a stable machine-readable <see cref="Code"/> and a
/// human-readable message. The auth controller maps it to a JSON <c>{ code, message }</c>
/// response so the frontend can render it inline in the form.
/// </summary>
public class AuthException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public AuthException(string code, string message, int statusCode = 400)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

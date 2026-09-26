namespace ConexyAI.Service.Email;

/// <summary>
/// EMAIL_VERIFICATION: sends the message that carries a confirmation code.
/// <para>
/// Kept as an interface with a single method so the sign-up flow can be exercised without a mail
/// server (the tests substitute a recorder), and so swapping the transport later touches nothing
/// else.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>Raises <see cref="EmailSendFailedException"/> when the message could not be handed over.</summary>
    Task SendVerificationCodeAsync(string toEmail, string code, int validMinutes, CancellationToken ct = default);

    /// <summary>
    /// PASSWORD_RESET: the same code, but the message says a password is being changed — the wording of
    /// the sign-up mail would make a user who is not signing up think their account is under attack and
    /// ignore it. Raised the same way on failure.
    /// </summary>
    Task SendPasswordResetCodeAsync(string toEmail, string code, int validMinutes, CancellationToken ct = default);
}

/// <summary>
/// The transport refused the message (bad credentials, no network, Gmail rate limit). Sign-up cannot
/// continue without the code, so this surfaces as its own error instead of a generic 500.
/// </summary>
public class EmailSendFailedException : Exception
{
    public EmailSendFailedException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

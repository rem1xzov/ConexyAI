namespace ConexyAI.Configuration;

/// <summary>
/// EMAIL_VERIFICATION: Gmail account used to send the 6-digit sign-up code.
/// <para>
/// The app password is a secret and is deliberately NOT part of appsettings.json — it is read from
/// the <c>SMTP_PASSWORD</c> environment variable (see <c>Program.cs</c>), so a leaked config file
/// cannot be used to send mail as us. Everything else (host, port, account, wording of the sender)
/// is ordinary configuration.
/// </para>
/// </summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>Environment variable that carries the app password; never a file.</summary>
    public const string PasswordEnvVar = "SMTP_PASSWORD";

    public string Host { get; set; } = "smtp.gmail.com";

    /// <summary>Submission port; STARTTLS is negotiated with <see cref="EnableSsl"/>.</summary>
    public int Port { get; set; } = 587;

    public string User { get; set; } = "";

    /// <summary>App password (16 characters for Google). Supplied through the environment only.</summary>
    public string Password { get; set; } = "";

    public string FromEmail { get; set; } = "";

    public string FromName { get; set; } = "ConexyAI";

    /// <summary>STARTTLS. Google refuses plain submission, so this stays on.</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>How long a code stays valid.</summary>
    public int CodeTtlMinutes { get; set; } = 15;

    /// <summary>Minimum gap between two codes for the same address / IP.</summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>Wrong guesses allowed for one code before a new one is required.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>False when the environment has no account: sign-up then answers with a clear error.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);
}

using ConexyAI.Configuration;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service;

// EMAIL_AUTH: добавлено 2026-09-19
public interface IEmailAuthService
{
    /// <summary>Creates a new email/password user (throws <see cref="AuthException"/> on conflict).</summary>
    Task<User> RegisterAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Authenticates an email/password user (throws <see cref="AuthException"/> on failure).</summary>
    Task<User> LoginAsync(string email, string password, CancellationToken ct = default);
}

public class EmailAuthService : IEmailAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IOptions<AdminAccountsOptions> _adminOptions;
    private readonly ILogger<EmailAuthService> _logger;

    public EmailAuthService(
        IUserRepository userRepository,
        IOptions<AdminAccountsOptions> adminOptions,
        ILogger<EmailAuthService> logger)
    {
        _userRepository = userRepository;
        _adminOptions = adminOptions;
        _logger = logger;
    }

    public async Task<User> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        ValidatePassword(password);

        var existing = await _userRepository.GetByEmailAsync(normalized, ct);
        if (existing is not null)
        {
            // A password-less account is a GitHub-only user; an email/password login can't work.
            if (existing.PasswordHash is null)
            {
                throw new AuthException(
                    "email_linked_to_github",
                    "Этот email привязан к входу через GitHub. Войдите через GitHub.",
                    409);
            }

            throw new AuthException("email_taken", "Этот email уже зарегистрирован.", 409);
        }

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            EmailConfirmed = false,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            SubscriptionTier = SubscriptionTier.Free,
            IsAdmin = _adminOptions.Value.Matches(normalized, null),
            CreatedAt = now,
            LastLoginAt = now
        };

        await _userRepository.AddAsync(user, ct);
        _logger.LogInformation("Email auth: registered new user {UserId} ({Email}).", user.Id, user.Email);
        return user;
    }

    public async Task<User> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);

        var user = await _userRepository.GetByEmailAsync(normalized, ct);
        if (user is null)
        {
            throw new AuthException("invalid_credentials", "Неверный email или пароль.", 401);
        }

        if (user.PasswordHash is null)
        {
            throw new AuthException(
                "email_linked_to_github",
                "Этот email привязан к входу через GitHub. Войдите через GitHub.",
                409);
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            throw new AuthException("invalid_credentials", "Неверный email или пароль.", 401);
        }

        // Re-evaluate admin status on every login (not only at creation) so newly-promoted
        // admins take effect without re-registering.
        user.IsAdmin = _adminOptions.Value.Matches(user.Email, user.GitHubUsername);
        user.LastLoginAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user, ct);

        return user;
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new AuthException("invalid_email", "Введите email.", 400);
        }

        var normalized = email.Trim().ToLowerInvariant();
        if (!IsValidEmail(normalized))
        {
            throw new AuthException("invalid_email", "Некорректный email.", 400);
        }

        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new AuthException("invalid_password", "Введите пароль.", 400);
        }

        if (password.Length < 8)
        {
            throw new AuthException("invalid_password", "Пароль должен содержать минимум 8 символов.", 400);
        }
    }

    // Minimal pragmatic check — avoids pulling in a full email-validation dependency.
    private static bool IsValidEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('@', at + 1) < 0;
    }
}

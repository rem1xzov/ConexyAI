using ConexyAI.Configuration;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service.Auth;
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
    // LOGIN_LOCKOUT: добавлено 2026-09-24 (ревью M20)
    private readonly ILoginAttemptTracker _attempts;

    // LOGIN_LOCKOUT: bcrypt-хэш случайного пароля. Для несуществующего email тоже выполняем Verify,
    // чтобы время ответа не выдавало, есть ли такой аккаунт.
    private static readonly string TimingEqualizerHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));

    public EmailAuthService(
        IUserRepository userRepository,
        IOptions<AdminAccountsOptions> adminOptions,
        ILogger<EmailAuthService> logger,
        ILoginAttemptTracker attempts)
    {
        _userRepository = userRepository;
        _adminOptions = adminOptions;
        _logger = logger;
        _attempts = attempts;
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
        // ADMIN_VERIFIED_ONLY: добавлено 2026-09-24 (ревью H1) — регистрация НИКОГДА не выдаёт админа:
        // email здесь ничем не подтверждён, совпадение с ADMIN_ACCOUNTS ничего не доказывает (раньше
        // любой аноним регистрировал email владельца и получал admin + SubscriptionTier.Admin).
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            EmailConfirmed = false,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            SubscriptionTier = SubscriptionTier.Free,
            IsAdmin = false,
            CreatedAt = now,
            LastLoginAt = now
        };
        // EMAIL_VERIFICATION_HOOK (ревью H1): когда появится подтверждение email, обработчик «email
        // подтверждён» должен выставить EmailConfirmed = true и ПОВТОРНО оценить админство:
        //     if (_adminOptions.Value.IsSuperAdmin(user)) { user.IsAdmin = true; user.SubscriptionTier = SubscriptionTier.Admin; }
        // и сохранить через IUserRepository.UpdateAsync (кэш проверки токена сбросится сам, isAdmin
        // в JWT подменяется значением из БД на каждом запросе — перевыпускать токен не нужно).

        await _userRepository.AddAsync(user, ct);
        // PRIVACY_LOGS: 2026-09-24 (ревью H3) — без email в логе, id достаточно.
        _logger.LogInformation("Email auth: registered new user {UserId}.", user.Id);
        return user;
    }

    public async Task<User> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);

        // LOGIN_LOCKOUT: добавлено 2026-09-24 (ревью M20) — 5 неудач подряд → 15 минут блокировки по
        // email. Проверяется ДО пароля (иначе блокировка не мешала бы перебору) и одинаково для
        // существующих и несуществующих аккаунтов.
        if (_attempts.GetLockout(normalized) is { } remaining)
        {
            throw AuthException.TooManyAttempts(remaining);
        }

        var user = await _userRepository.GetByEmailAsync(normalized, ct);
        if (user is null)
        {
            BCrypt.Net.BCrypt.Verify(password ?? string.Empty, TimingEqualizerHash);
            throw Failed(normalized, new AuthException("invalid_credentials", "Неверный email или пароль.", 401));
        }

        if (user.PasswordHash is null)
        {
            throw Failed(normalized, new AuthException(
                "email_linked_to_github",
                "Этот email привязан к входу через GitHub. Войдите через GitHub.",
                409));
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            throw Failed(normalized, new AuthException("invalid_credentials", "Неверный email или пароль.", 401));
        }

        _attempts.Reset(normalized);

        // ADMIN_UNLIMITED: добавлено 2026-09-19 — promote ADMIN_ACCOUNTS superadmins on
        // every login, but never demote a make-admin'd user (their IsAdmin lives in the DB).
        // ADMIN_VERIFIED_ONLY 2026-09-24 (ревью H1): только по подтверждённому email / GitHub-личности
        // (IsSuperAdmin); у email/пароль-аккаунта сейчас EmailConfirmed всегда false, так что
        // до появления подтверждения email здесь никто не повышается.
        if (_adminOptions.Value.IsSuperAdmin(user))
        {
            user.IsAdmin = true;
            user.SubscriptionTier = SubscriptionTier.Admin;
        }
        user.LastLoginAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user, ct);

        return user;
    }

    // LOGIN_LOCKOUT: добавлено 2026-09-24 — считает неудачу; неудача, включившая блокировку, сразу
    // отвечает 429, чтобы пользователь увидел, что дальше пробовать бесполезно.
    private AuthException Failed(string normalizedEmail, AuthException failure)
    {
        if (_attempts.RegisterFailure(normalizedEmail) is { } lockout)
        {
            _logger.LogWarning("Email auth: too many failed logins, account key locked for {Minutes} min.", lockout.TotalMinutes);
            return AuthException.TooManyAttempts(lockout);
        }

        return failure;
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new AuthException("invalid_email", "Введите email.", 400);
        }

        var normalized = email.Trim().ToLowerInvariant();
        // LOGIN_LOCKOUT: 2026-09-24 — колонка users.Email ограничена 320 символами; длиннее — это не
        // email, а мусор (и лишний ключ в кэше блокировок).
        if (normalized.Length > 320 || !IsValidEmail(normalized))
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

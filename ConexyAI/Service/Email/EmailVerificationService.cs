using System.Globalization;
using System.Security.Cryptography;
using ConexyAI.Configuration;
using ConexyAI.Entity;
using ConexyAI.Model;
using ConexyAI.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Service.Email;

/// <summary>
/// EMAIL_VERIFICATION: sign-up in two steps — address and password first, the 6-digit code after.
/// <para>
/// No token is issued until the code is confirmed, and no account exists before that either: the
/// pending password lives in the code row (see <see cref="EmailVerificationCodeEntity"/>). Existing
/// accounts are unaffected — they are all confirmed (the migration says so) and log in as before.
/// </para>
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>Stores the pending sign-up and mails a code. Throws <see cref="AuthException"/> on conflict.</summary>
    Task<VerificationChallenge> StartRegistrationAsync(string email, string password, string? ip, CancellationToken ct = default);

    /// <summary>Confirms the code and returns the (now existing, confirmed) user.</summary>
    Task<User> VerifyAsync(string email, string code, CancellationToken ct = default);

    /// <summary>Issues a fresh code for a pending sign-up, outside the cooldown.</summary>
    Task<VerificationChallenge> ResendAsync(string email, string? ip, CancellationToken ct = default);
}

/// <summary>What the client needs to render the code step.</summary>
public sealed record VerificationChallenge(string Email, int ResendCooldownSeconds);

/// <summary>
/// The code lifecycle rules, free of I/O so they can be exercised without a database or a mail
/// server.
/// </summary>
public static class EmailCodePolicy
{
    /// <summary>Cryptographically strong 6-digit code (100000–999999) — never <c>Random</c>.</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(CultureInfo.InvariantCulture);

    /// <summary>Seconds left before another code may be requested; 0 when it may be requested now.</summary>
    public static int CooldownRemaining(DateTime now, DateTime lastSentAt, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0)
        {
            return 0;
        }

        var elapsed = (now - lastSentAt).TotalSeconds;
        return elapsed >= cooldownSeconds ? 0 : Math.Max(1, (int)Math.Ceiling(cooldownSeconds - elapsed));
    }

    public static bool IsExpired(DateTime now, DateTime expiresAt) => now >= expiresAt;

    /// <summary>Attempts left after one wrong guess; never below zero.</summary>
    public static int SpendAttempt(int attemptsLeft) => Math.Max(0, attemptsLeft - 1);
}

public class EmailVerificationService : IEmailVerificationService
{
    private readonly IUserRepository _users;
    private readonly IEmailVerificationRepository _codes;
    private readonly IEmailSender _mail;
    private readonly IOptions<SmtpOptions> _options;
    private readonly IOptions<AdminAccountsOptions> _adminOptions;
    private readonly ILogger<EmailVerificationService> _logger;
    // EMAIL_VERIFICATION: the cooldown and the code lifetime are the two things worth testing, so the
    // clock is injected (the same way LoginAttemptTracker does it) instead of read from DateTime.
    private readonly TimeProvider _time;

    public EmailVerificationService(
        IUserRepository users,
        IEmailVerificationRepository codes,
        IEmailSender mail,
        IOptions<SmtpOptions> options,
        IOptions<AdminAccountsOptions> adminOptions,
        ILogger<EmailVerificationService> logger,
        TimeProvider time)
    {
        _users = users;
        _codes = codes;
        _mail = mail;
        _options = options;
        _adminOptions = adminOptions;
        _logger = logger;
        _time = time;
    }

    public async Task<VerificationChallenge> StartRegistrationAsync(
        string email, string password, string? ip, CancellationToken ct = default)
    {
        var settings = _options.Value;
        if (!settings.IsConfigured)
        {
            // Better a clear "mail is not set up here" than an account nobody can confirm.
            throw new AuthException("email_not_configured", "Отправка писем не настроена на сервере.", 503);
        }

        // The same validation the login path uses, so a password accepted here is accepted there.
        var normalized = EmailAuthService.NormalizeEmail(email);
        EmailAuthService.ValidatePassword(password);

        var existing = await _users.GetByEmailAsync(normalized, ct);
        if (existing is not null)
        {
            if (existing.PasswordHash is null)
            {
                throw new AuthException(
                    "email_linked_to_github",
                    "Этот email привязан к входу через GitHub. Войдите через GitHub.",
                    409);
            }

            // A confirmed account is genuinely taken. An unconfirmed one cannot exist: accounts are
            // created only by a confirmed code.
            throw new AuthException("email_taken", "Этот email уже зарегистрирован.", 400);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await EnforceCooldownAsync(normalized, ip, now, ct);

        var code = EmailCodePolicy.GenerateCode();
        await _codes.ReplaceAsync(
            new EmailVerificationCodeEntity
            {
                Email = normalized,
                CodeHash = BCrypt.Net.BCrypt.HashPassword(code),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                ExpiresAt = now.AddMinutes(settings.CodeTtlMinutes),
                AttemptsLeft = settings.MaxAttempts,
                LastSentIp = ip,
                CreatedAt = now,
            },
            ct);

        await DeliverAsync(normalized, code, settings, ct);
        _logger.LogInformation("Email verification: sign-up started, code sent.");
        return new VerificationChallenge(normalized, settings.ResendCooldownSeconds);
    }

    public async Task<User> VerifyAsync(string email, string code, CancellationToken ct = default)
    {
        var settings = _options.Value;
        var normalized = EmailAuthService.NormalizeEmail(email);
        var now = _time.GetUtcNow().UtcDateTime;

        var row = await _codes.GetLatestAsync(normalized, ct);
        if (row is null)
        {
            throw new AuthException("code_expired", "Код не найден. Запросите новый.", 400);
        }

        if (EmailCodePolicy.IsExpired(now, row.ExpiresAt))
        {
            // The row is deliberately NOT deleted: its only remaining job is to carry the pending
            // password hash so "отправить код повторно" can issue a new code for this same sign-up
            // instead of making the user fill the whole form again. Expired rows are dropped by the
            // next replace or by a successful confirmation.
            throw new AuthException("code_expired", "Код истёк. Запросите новый.", 400);
        }

        if (row.AttemptsLeft <= 0)
        {
            throw new AuthException("code_attempts_exhausted", "Попытки исчерпаны. Запросите новый код.", 400);
        }

        // A wrong code spends one attempt. The counter lives on the row, so it survives a page reload
        // and cannot be reset by simply retrying.
        var typed = (code ?? string.Empty).Trim();
        if (typed.Length != 6 || !BCrypt.Net.BCrypt.Verify(typed, row.CodeHash))
        {
            row.AttemptsLeft = EmailCodePolicy.SpendAttempt(row.AttemptsLeft);
            await _codes.UpdateAsync(row, ct);

            if (row.AttemptsLeft <= 0)
            {
                throw new AuthException("code_attempts_exhausted", "Попытки исчерпаны. Запросите новый код.", 400);
            }

            throw new AuthException(
                "invalid_code",
                $"Неверный код. Осталось попыток: {row.AttemptsLeft}.",
                400) { AttemptsLeft = row.AttemptsLeft };
        }

        // Confirmed: the account comes into existence here, already verified.
        var user = await _users.GetByEmailAsync(normalized, ct);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalized,
                EmailConfirmed = true,
                PasswordHash = row.PasswordHash,
                SubscriptionTier = SubscriptionTier.Free,
                IsAdmin = false,
                CreatedAt = row.CreatedAt,
                LastLoginAt = now,
            };
            await _users.AddAsync(user, ct);
            _logger.LogInformation("Email verification: account created for the confirmed address (user {UserId}).", user.Id);
        }
        else
        {
            // Only reachable if the row survived an account created by another path; the freshly
            // chosen password is the one the user expects, so it wins.
            user.EmailConfirmed = true;
            user.PasswordHash = row.PasswordHash;
            user.LastLoginAt = now;
            await _users.UpdateAsync(user, ct);
        }

        // EMAIL_VERIFICATION_HOOK (ревью H1): only now is the address actually proven, so this is the
        // first moment <c>ADMIN_ACCOUNTS</c> may be trusted. Never demotes: IsAdmin also comes from
        // the admin panel.
        if (_adminOptions.Value.IsSuperAdmin(user))
        {
            user.IsAdmin = true;
            user.SubscriptionTier = SubscriptionTier.Admin;
            await _users.UpdateAsync(user, ct);
        }

        await _codes.RemoveAllForEmailAsync(normalized, ct);
        _ = settings;
        return user;
    }

    public async Task<VerificationChallenge> ResendAsync(string email, string? ip, CancellationToken ct = default)
    {
        var settings = _options.Value;
        if (!settings.IsConfigured)
        {
            throw new AuthException("email_not_configured", "Отправка писем не настроена на сервере.", 503);
        }

        var normalized = EmailAuthService.NormalizeEmail(email);
        var now = _time.GetUtcNow().UtcDateTime;

        var row = await _codes.GetLatestAsync(normalized, ct);
        if (row is null)
        {
            // Nothing pending: the user has to start over (or the code was already used).
            throw new AuthException("code_expired", "Регистрация не начата. Заполните форму заново.", 400);
        }

        await EnforceCooldownAsync(normalized, ip, now, ct);

        var code = EmailCodePolicy.GenerateCode();
        row.CodeHash = BCrypt.Net.BCrypt.HashPassword(code);
        row.ExpiresAt = now.AddMinutes(settings.CodeTtlMinutes);
        row.AttemptsLeft = settings.MaxAttempts;
        row.CreatedAt = now;
        row.LastSentIp = ip;
        await _codes.UpdateAsync(row, ct);

        await DeliverAsync(normalized, code, settings, ct);
        _logger.LogInformation("Email verification: code re-sent.");
        return new VerificationChallenge(normalized, settings.ResendCooldownSeconds);
    }

    /// <summary>
    /// The 60-second rule, checked per address and per caller. Both are needed: per address stops a
    /// user being flooded, per IP stops one client farming codes for many addresses.
    /// </summary>
    private async Task EnforceCooldownAsync(string email, string? ip, DateTime now, CancellationToken ct)
    {
        var settings = _options.Value;

        var forEmail = await _codes.GetLatestAsync(email, ct);
        if (forEmail is not null)
        {
            ThrowIfCoolingDown(EmailCodePolicy.CooldownRemaining(now, forEmail.CreatedAt, settings.ResendCooldownSeconds));
        }

        if (!string.IsNullOrWhiteSpace(ip))
        {
            var forIp = await _codes.GetLatestForIpAsync(ip, ct);
            if (forIp is not null)
            {
                ThrowIfCoolingDown(EmailCodePolicy.CooldownRemaining(now, forIp.CreatedAt, settings.ResendCooldownSeconds));
            }
        }
    }

    // 400 rather than 429: this is a "wait a moment" state the form shows with its own countdown, not
    // a rate limit the client should treat as abuse. retryAfterSeconds carries the remaining time.
    private static void ThrowIfCoolingDown(int remainingSeconds)
    {
        if (remainingSeconds <= 0)
        {
            return;
        }

        throw new AuthException(
            "resend_cooldown",
            $"Код уже отправлен. Повторить можно через {remainingSeconds} сек.",
            400,
            remainingSeconds);
    }

    private async Task DeliverAsync(string email, string code, SmtpOptions settings, CancellationToken ct)
    {
        try
        {
            await _mail.SendVerificationCodeAsync(email, code, settings.CodeTtlMinutes, ct);
        }
        catch (EmailSendFailedException ex)
        {
            // Nobody received this code, so it must not hold the address in its cooldown: drop it and
            // let the next attempt send a fresh one straight away.
            await _codes.RemoveAllForEmailAsync(email, ct);
            _logger.LogError(ex, "Email verification: could not deliver the code.");
            throw new AuthException("email_send_failed", "Не удалось отправить письмо. Попробуйте позже.", 502);
        }
    }
}

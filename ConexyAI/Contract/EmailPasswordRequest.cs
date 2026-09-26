namespace ConexyAI.Contract;

// EMAIL_AUTH: добавлено 2026-09-19
/// <summary>Credentials for email/password registration and login.</summary>
/// <remarks>
/// PRIVACY_POLICY: изменено 2026-09-25 — <paramref name="AcceptedPolicy"/> несёт отметку о согласии
/// с Политикой обработки персональных данных. Поле опционально с дефолтом <c>false</c>, поэтому
/// запросы входа и старые клиенты продолжают работать; регистрация без <c>true</c> отклоняется,
/// чтобы сервер не полагался на клиентскую блокировку кнопки.
/// </remarks>
public record EmailPasswordRequest(string Email, string Password, bool AcceptedPolicy = false);

// EMAIL_VERIFICATION: добавлено 2026-09-24
/// <summary>
/// The 6-digit code the user was mailed. The password is NOT sent again: the pending sign-up already
/// holds its hash (see <c>EmailVerificationCodeEntity</c>), so a leaked code alone cannot change the
/// password of anything that already exists.
/// </summary>
public record EmailVerificationRequest(string Email, string Code);

/// <summary>Request of a fresh code for a pending sign-up.</summary>
public record EmailOnlyRequest(string Email);

// PASSWORD_RESET: добавлено 2026-09-26
/// <summary>
/// Confirms a password reset: the code that was mailed, plus the password the user wants instead. Both
/// travel together because the code is sent before the form asks for the password.
/// </summary>
public record PasswordResetRequest(string Email, string Code, string NewPassword);

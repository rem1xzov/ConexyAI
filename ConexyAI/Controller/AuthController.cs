using System.Collections.Concurrent;
using System.Security.Cryptography;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Auth;
// EMAIL_VERIFICATION: добавлено 2026-09-24
using ConexyAI.Service.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConexyAI.Controller;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const string StateCookieName = "github_oauth_state";
    // GITHUB_OAUTH: добавлено 2026-09-19
    private const string AuthCookieName = "conexy_auth";

    // MOBILE_HANDOFF: вход из Android-приложения. Google запрещает OAuth внутри WebView, поэтому
    // логин идёт в системном браузере, а сессия возвращается приложению одноразовым кодом:
    // браузер → conexy.app://auth?code=… → приложение меняет код на JWT.
    private const string MobileRedirectUri = "conexy.app://auth";
    private const string MobileRedirectCookie = "conexy_mobile_redirect";
    private static readonly TimeSpan MobileHandoffTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (TokenResponse Token, DateTime ExpiresUtc)> MobileHandoffs = new();

    private readonly ITokenService _tokenService;
    private readonly IWebHostEnvironment _environment;
    // GITHUB_OAUTH: добавлено 2026-09-19
    private readonly IGitHubOAuthService _gitHubOAuthService;
    private readonly GitHubOAuthOptions _gitHubOAuthOptions;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<AuthController> _logger;
    // EMAIL_AUTH: добавлено 2026-09-19
    private readonly IEmailAuthService _emailAuthService;
    // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
    private readonly ITokenRevocationValidator _revocation;
    // EMAIL_VERIFICATION: добавлено 2026-09-24
    private readonly IEmailVerificationService _emailVerification;

    public AuthController(
        ITokenService tokenService,
        IWebHostEnvironment environment,
        IGitHubOAuthService gitHubOAuthService,
        IOptions<GitHubOAuthOptions> gitHubOAuthOptions,
        IUserRepository userRepository,
        ILogger<AuthController> logger,
        IEmailAuthService emailAuthService,
        ITokenRevocationValidator revocation,
        IEmailVerificationService emailVerification)
    {
        _tokenService = tokenService;
        _environment = environment;
        _gitHubOAuthService = gitHubOAuthService;
        _gitHubOAuthOptions = gitHubOAuthOptions.Value;
        _userRepository = userRepository;
        _logger = logger;
        _emailAuthService = emailAuthService;
        _revocation = revocation;
        _emailVerification = emailVerification;
    }

    /// <summary>
    /// Issues a signed JWT for local development/testing only. In production the
    /// token must come from the real identity provider, so this endpoint is
    /// hidden (404) outside the Development environment.
    /// </summary>
    [HttpPost("dev-token")]
    public async Task<ActionResult<TokenResponse>> CreateDevToken(
        [FromQuery] Guid? userId = null,
        CancellationToken ct = default)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var effectiveUserId = userId ?? Guid.NewGuid();

        // GITHUB_OAUTH: добавлено 2026-09-19 — ensure the dev user exists so the new
        // FK constraints on user-scoped tables stay satisfiable during local testing.
        await _userRepository.EnsureExistsAsync(effectiveUserId, ct);

        // TOKEN_REVOCATION: 2026-09-24 — токен несёт текущие TokenVersion/IsAdmin из БД.
        var user = await _userRepository.GetByIdAsync(effectiveUserId, ct);
        return Ok(_tokenService.CreateToken(user!));
    }

    // GITHUB_OAUTH: добавлено 2026-09-19
    /// <summary>Starts the GitHub OAuth flow: sets a CSRF <c>state</c> cookie and redirects to GitHub.</summary>
    [HttpGet("github/login")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public IActionResult GitHubLogin() => StartGitHubLogin();

    // MOBILE_HANDOFF: точка входа для приложения. Если пользователь уже вошёл на сайте в этом
    // браузере — сессия отдаётся приложению сразу, без повторного OAuth.
    /// <summary>Starts the login flow for the Android app and returns the session via a one-time code.</summary>
    [HttpGet("mobile")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public async Task<IActionResult> MobileLogin([FromQuery] string? redirect, CancellationToken ct)
    {
        // MOBILE_REDIRECT: только наш собственный scheme — иначе это открытый редирект с токеном.
        if (!string.Equals(redirect, MobileRedirectUri, StringComparison.Ordinal))
            return BadRequest(new { error = "unsupported_redirect" });

        if (Request.Cookies[AuthCookieName] is { Length: > 0 } cookie
            && await ValidateCookieTokenAsync(cookie, ct) is { } session)
        {
            _logger.LogInformation("Mobile login: reusing the browser session for {UserId}.", session.Principal.FindFirst("sub")?.Value);
            return MobileReturn(session.Response);
        }

        Response.Cookies.Append(MobileRedirectCookie, MobileRedirectUri, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        return StartGitHubLogin();
    }

    // MOBILE_HANDOFF: обмен одноразового кода на сессию. Код живёт две минуты и удаляется при
    // первом использовании, поэтому перехват deep link не даёт долговременного доступа.
    /// <summary>Exchanges the one-time handoff code from the app link for a JWT.</summary>
    [HttpPost("handoff")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public IActionResult ExchangeHandoff([FromBody] MobileHandoffRequest? request)
    {
        if (request?.Code is not { Length: > 0 } code || !MobileHandoffs.TryRemove(code, out var entry))
            return BadRequest(new { error = "code_expired" });

        if (entry.ExpiresUtc < DateTime.UtcNow)
            return BadRequest(new { error = "code_expired" });

        return Ok(entry.Token);
    }

    private IActionResult StartGitHubLogin()
    {
        var state = GenerateState();

        // Short-lived, HttpOnly CSRF state bound to this browser session. SameSite=Lax still
        // sends the cookie on the top-level GET redirect back from GitHub.
        Response.Cookies.Append(StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        return Redirect(_gitHubOAuthService.BuildLoginUrl(state));
    }

    // GITHUB_OAUTH: добавлено 2026-09-19
    /// <summary>
    /// Handles the GitHub OAuth callback: validates state, exchanges the code, upserts the
    /// user, issues a real JWT, stores it in an httpOnly cookie and redirects the SPA to a
    /// clean URL (no fragment). The SPA then fetches the JWT via <c>GET /api/auth/session</c>.
    /// </summary>
    [HttpGet("github/callback")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public async Task<IActionResult> GitHubCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        var frontendBase = FrontendBaseUrl();

        // GITHUB_OAUTH: добавлено 2026-09-19 — debug logging.
        _logger.LogInformation(
            "GitHub callback hit: code prefix '{CodePrefix}', state present {StatePresent}, cookie present {CookiePresent}.",
            CodePrefix(code), !string.IsNullOrWhiteSpace(state), !string.IsNullOrWhiteSpace(Request.Cookies[StateCookieName]));

        if (string.IsNullOrWhiteSpace(code))
        {
            _logger.LogWarning("GitHub callback: missing code, redirecting to error.");
            return RedirectToFrontendError(frontendBase, "missing_code");
        }

        if (!ValidateState(state))
        {
            _logger.LogWarning("GitHub callback: state mismatch/absent, redirecting to error.");
            return RedirectToFrontendError(frontendBase, "state_mismatch");
        }

        try
        {
            var result = await _gitHubOAuthService.HandleCallbackAsync(code, ct);
            var token = _tokenService.CreateToken(result.UserId, result.IsAdmin, result.TokenVersion);

            // GITHUB_OAUTH: добавлено 2026-09-19 — deliver the JWT via an httpOnly cookie
            // (not a URL fragment), so a repeated callback can no longer clobber it. The SPA
            // reads it back through GET /api/auth/session and stores it in localStorage.
            SetAuthCookie(token);

            // MOBILE_HANDOFF: вход начали из приложения — вернём сессию туда, а не на сайт.
            if (Request.Cookies[MobileRedirectCookie] == MobileRedirectUri)
            {
                Response.Cookies.Delete(MobileRedirectCookie);
                _logger.LogInformation("GitHub callback success: handing the session back to the app.");
                return MobileReturn(token);
            }

            var redirectUrl = $"{frontendBase}/";
            _logger.LogInformation("GitHub callback success: set httpOnly cookie and redirecting to '{RedirectUrl}'.", redirectUrl);
            return Redirect(redirectUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitHub callback failed for code prefix '{CodePrefix}'.", CodePrefix(code));
            // ERROR_REDACTION: 2026-09-24 (ревью L12) — раньше в URL уходил ex.Message, а в нём бывает
            // сырой ответ GitHub. Подробности только в логе, клиенту — стабильный код.
            return RedirectToFrontendError(frontendBase, "github_auth_failed");
        }
    }

    // GITHUB_OAUTH: добавлено 2026-09-19
    /// <summary>
    /// Returns the JWT stored in the httpOnly <c>conexy_auth</c> cookie (set by the OAuth
    /// callback) after re-validating its signature and lifetime. The SPA calls this on mount
    /// and persists the token to localStorage for <c>Authorization: Bearer</c> + SignalR.
    /// Returns 401 when there is no (valid) session.
    /// </summary>
    [HttpGet("session")]
    public async Task<ActionResult<TokenResponse>> GetSession(CancellationToken ct)
    {
        var token = Request.Cookies[AuthCookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized();
        }

        // TOKEN_REVOCATION: 2026-09-24 (ревью M19) — cookie-токен проходит ту же проверку, что и
        // Bearer: отозванный (logout на другом устройстве, разжалование) или токен удалённого
        // пользователя больше не выдаётся SPA.
        var validated = await ValidateCookieTokenAsync(token, ct);
        if (validated is null)
        {
            // Expired/invalid/revoked: drop the stale cookie so the client cleanly re-authenticates.
            Response.Cookies.Delete(AuthCookieName);
            return Unauthorized();
        }

        return Ok(validated.Response);
    }

    // EMAIL_AUTH: изменено 2026-09-24 (EMAIL_VERIFICATION) — регистрация больше не выдаёт токен: она
    // запоминает выбранный пароль и отправляет на адрес 6-значный код, а аккаунт появляется только в
    // verify-email. Так неподтверждённых пользователей в базе не бывает вообще.
    /// <summary>
    /// Starts a sign-up: validates the address and password, mails a 6-digit code and returns the
    /// challenge. No JWT is issued here — the account does not exist yet.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting(AuthRateLimitPolicies.Register)]
    public async Task<IActionResult> Register([FromBody] EmailPasswordRequest request, CancellationToken ct)
    {
        // PRIVACY_POLICY: добавлено 2026-09-25 — согласие обязательно и проверяется здесь, а не только
        // галочкой в форме: клиентская блокировка обходится обычным POST на этот эндпоинт.
        if (!request.AcceptedPolicy)
        {
            return AuthError(new AuthException(
                "policy_not_accepted",
                "Для регистрации необходимо согласие с Политикой обработки персональных данных.",
                400));
        }

        try
        {
            var challenge = await _emailVerification.StartRegistrationAsync(
                request.Email, request.Password, ClientIp(), ct);

            return Ok(new
            {
                success = true,
                requireVerification = true,
                email = challenge.Email,
                resendCooldownSeconds = challenge.ResendCooldownSeconds,
            });
        }
        catch (AuthException ex)
        {
            return AuthError(ex);
        }
    }

    // EMAIL_VERIFICATION: добавлено 2026-09-24
    /// <summary>
    /// Confirms a sign-up with the 6-digit code. On success the account is created (already
    /// confirmed) and a full session is issued, so the client is logged in without a second step.
    /// </summary>
    [HttpPost("verify-email")]
    [EnableRateLimiting(AuthRateLimitPolicies.VerifyEmail)]
    public async Task<IActionResult> VerifyEmail([FromBody] EmailVerificationRequest request, CancellationToken ct)
    {
        try
        {
            var user = await _emailVerification.VerifyAsync(request.Email, request.Code, ct);
            _logger.LogInformation("Email verification: account confirmed (user {UserId}).", user.Id);
            return Ok(IssueSession(user));
        }
        catch (AuthException ex)
        {
            return AuthError(ex);
        }
    }

    // EMAIL_VERIFICATION: добавлено 2026-09-24
    /// <summary>Sends a fresh code for a pending sign-up, outside the 60-second cooldown.</summary>
    [HttpPost("resend-code")]
    [EnableRateLimiting(AuthRateLimitPolicies.ResendCode)]
    public async Task<IActionResult> ResendCode([FromBody] EmailOnlyRequest request, CancellationToken ct)
    {
        try
        {
            var challenge = await _emailVerification.ResendAsync(request.Email, ClientIp(), ct);
            return Ok(new { success = true, resendCooldownSeconds = challenge.ResendCooldownSeconds });
        }
        catch (AuthException ex)
        {
            return AuthError(ex);
        }
    }

    // PASSWORD_RESET: добавлено 2026-09-26
    /// <summary>
    /// Starts a password reset. The address must already have a password account; the mailed code then
    /// proves the requester owns it. No session is issued here — the password is chosen at
    /// <see cref="ResetPassword"/>, together with that code.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting(AuthRateLimitPolicies.ForgotPassword)]
    public async Task<IActionResult> ForgotPassword([FromBody] EmailOnlyRequest request, CancellationToken ct)
    {
        try
        {
            var challenge = await _emailVerification.StartPasswordResetAsync(request.Email, ClientIp(), ct);
            return Ok(new
            {
                success = true,
                email = challenge.Email,
                resendCooldownSeconds = challenge.ResendCooldownSeconds,
            });
        }
        catch (AuthException ex)
        {
            return AuthError(ex);
        }
    }

    // PASSWORD_RESET: добавлено 2026-09-26
    /// <summary>
    /// Confirms the reset code and applies the new password. The account's existing sessions are revoked
    /// in the process, and a fresh one is issued to the caller.
    /// </summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting(AuthRateLimitPolicies.ResetPassword)]
    public async Task<IActionResult> ResetPassword([FromBody] PasswordResetRequest request, CancellationToken ct)
    {
        try
        {
            var user = await _emailVerification.ResetPasswordAsync(
                request.Email, request.Code, request.NewPassword, ct);
            _logger.LogInformation("Password reset: password changed, session issued (user {UserId}).", user.Id);
            return Ok(IssueSession(user));
        }
        catch (AuthException ex)
        {
            return AuthError(ex);
        }
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Logs in with an email/password account.</summary>
    [HttpPost("login")]
    [EnableRateLimiting(AuthRateLimitPolicies.Login)]
    public async Task<IActionResult> Login([FromBody] EmailPasswordRequest request, CancellationToken ct)
    {
        try
        {
            var user = await _emailAuthService.LoginAsync(request.Email, request.Password, ct);
            return Ok(IssueSession(user));
        }
        catch (AuthException ex)
        {
            return AuthError(ex);
        }
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>
    /// Logs out: revokes every JWT of the user (all devices) and clears the session cookie.
    /// </summary>
    /// <remarks>
    /// TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19, H10) — раньше logout только стирал cookie,
    /// а сам 30-дневный JWT (localStorage, SignalR) продолжал работать. Теперь версия токенов
    /// пользователя увеличивается, и все выданные ранее токены отклоняются — на ВСЕХ устройствах
    /// (отдельных сессий у нас нет; это осознанная цена). Пользователь определяется по валидному
    /// Bearer-токену или по cookie; уже отозванный токен ничего не меняет (нельзя «разлогинивать»
    /// жертву утёкшим старым токеном).
    /// </remarks>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Guid? userId = null;
        if (User.Identity?.IsAuthenticated == true && User.TryGetUserId(out var bearerUserId))
        {
            // The bearer already passed OnTokenValidated (signature + revocation check).
            userId = bearerUserId;
        }
        else if (Request.Cookies[AuthCookieName] is { Length: > 0 } cookieToken
                 && await ValidateCookieTokenAsync(cookieToken, ct) is { } validated
                 && validated.Principal.TryGetUserId(out var cookieUserId))
        {
            userId = cookieUserId;
        }

        if (userId is { } id)
        {
            await _userRepository.BumpTokenVersionAsync(id, ct);
            _logger.LogInformation("Auth: user {UserId} logged out; all their tokens are revoked.", id);
        }

        Response.Cookies.Delete(AuthCookieName);
        return Ok(new { success = true });
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Returns the authenticated user's profile for the account widget.</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserProfileDto>> GetMe(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return Unauthorized();

        var displayName = user.GitHubUsername ?? user.Email ?? "Пользователь";
        return Ok(new UserProfileDto(
            user.Email ?? string.Empty,
            displayName,
            // TIER_DISPLAY: тариф с учётом срока — истёкшая подписка показывается как Free.
            SubscriptionService.EffectiveTier(user, DateTime.UtcNow).ToString(),
            // ADMIN_PANEL: добавлено 2026-09-19 — use the same claim the backend's admin checks use.
            // TOKEN_REVOCATION 2026-09-24: этот claim уже подменён значением из БД (OnTokenValidated).
            User.IsAdmin()));
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    private TokenResponse IssueSession(User user)
    {
        var token = _tokenService.CreateToken(user);
        SetAuthCookie(token);
        return token;
    }

    // LOGIN_LOCKOUT: добавлено 2026-09-24 (ревью M20) — ошибки формы остаются { code, message };
    // блокировка отвечает 429 в том же формате, что и rate limiter: { error: "TOO_MANY_ATTEMPTS",
    // retryAfterSeconds, code, message } + заголовок Retry-After.
    private IActionResult AuthError(AuthException ex)
    {
        if (ex.StatusCode == StatusCodes.Status429TooManyRequests)
        {
            var retryAfter = ex.RetryAfterSeconds ?? 60;
            Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(ex.StatusCode, AuthSecurityExtensions.TooManyAttemptsBody(retryAfter, ex.Message));
        }

        // EMAIL_VERIFICATION: retryAfterSeconds (the resend cooldown) and attemptsLeft (the remaining
        // guesses for a code) travel with the error so the form can show both without guessing.
        return StatusCode(ex.StatusCode, new
        {
            code = ex.Code,
            message = ex.Message,
            retryAfterSeconds = ex.RetryAfterSeconds,
            attemptsLeft = ex.AttemptsLeft,
        });
    }

    /// <summary>
    /// The caller's address, for the per-IP resend cooldown. Forwarded headers are already applied by
    /// the pipeline, so this is the real client address rather than the proxy's.
    /// </summary>
    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    // TOKEN_REVOCATION: добавлено 2026-09-24 — подпись/срок + отзыв (tv, существование пользователя).
    private async Task<ValidatedToken?> ValidateCookieTokenAsync(string token, CancellationToken ct)
    {
        var validated = _tokenService.ValidateToken(token);
        if (validated is null)
            return null;

        var check = await _revocation.CheckAsync(validated.Principal, ct);
        return check.IsValid ? validated : null;
    }

    private void SetAuthCookie(TokenResponse token)
    {
        // EMAIL_AUTH: добавлено 2026-09-19 — Expires is set explicitly so the browser treats
        // the cookie as persistent (not a session cookie). It lives exactly as long as the JWT
        // (see JwtOptions.AccessTokenLifetimeMinutes — 90 days by default), enabling "remember me"
        // across browser restarts.
        // SESSION_LIFETIME: добавлено 2026-09-26 — если сайт открыт по http, а не по https, браузер
        // выбрасывает эту Secure-cookie, и тогда сессия не переживёт даже перезагрузки страницы;
        // клиент вытягивает это своим токеном, но причину стоит искать здесь.
        Response.Cookies.Append(AuthCookieName, token.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = token.ExpiresAtUtc
        });
    }

    private static string GenerateState()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>First 8 characters of the code (or a marker), for debug logging only.</summary>
    private static string CodePrefix(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "<empty>" : code[..Math.Min(8, code.Length)];

    private bool ValidateState(string? state)
    {
        var expected = Request.Cookies[StateCookieName];
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        // Remove the cookie immediately: state is single-use.
        Response.Cookies.Delete(StateCookieName);
        return string.Equals(expected, state, StringComparison.Ordinal);
    }

    private string FrontendBaseUrl()
    {
        // The SPA and the API are same-origin in production, so the frontend origin equals
        // the callback URL's origin.
        var callback = new Uri(_gitHubOAuthOptions.CallbackUrl);
        return callback.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Creates a one-time code and returns a tiny page that sends the app its deep link.</summary>
    /// <remarks>
    /// MOBILE_RETURN: именно страница, а не 302 на custom scheme — браузеры по-разному относятся
    /// к редиректу на незнакомую схему без жеста пользователя. Здесь есть и автоматический переход,
    /// и кнопка, если автопереход заблокирован.
    /// </remarks>
    private IActionResult MobileReturn(TokenResponse token)
    {
        var code = Guid.NewGuid().ToString("N");
        MobileHandoffs[code] = (token, DateTime.UtcNow.Add(MobileHandoffTtl));

        var now = DateTime.UtcNow;
        foreach (var stale in MobileHandoffs.Where(pair => pair.Value.ExpiresUtc < now).Select(pair => pair.Key).ToList())
            MobileHandoffs.TryRemove(stale, out _);

        var link = $"{MobileRedirectUri}?code={Uri.EscapeDataString(code)}";
        Response.Headers.CacheControl = "no-store";
        return Content(
            "<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>ConexyAI</title></head>" +
            "<body style=\"margin:0;height:100vh;display:flex;flex-direction:column;gap:16px;align-items:center;" +
            "justify-content:center;background:#0a0a0a;color:#f2f4f8;font:16px/1.5 -apple-system,Segoe UI,Roboto,sans-serif\">" +
            "<p style=\"margin:0\">Возвращаемся в приложение…</p>" +
            $"<a style=\"color:#4fd1bd\" href=\"{link}\">Открыть ConexyAI</a>" +
            $"<script>window.location.replace({System.Text.Json.JsonSerializer.Serialize(link)});</script>" +
            "</body></html>",
            "text/html; charset=utf-8");
    }

    private RedirectResult RedirectToFrontendError(string frontendBase, string message)
    {
        return Redirect($"{frontendBase}/?auth=error&message={Uri.EscapeDataString(message)}");
    }
}

// MOBILE_HANDOFF: тело запроса приложения — одноразовый код из deep link.
public record MobileHandoffRequest(string? Code);

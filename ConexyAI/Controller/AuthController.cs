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
    // SEC_COOKIE_PREFIX: префикс __Host- запрещает браузеру принимать эту cookie с Domain=, без Secure или
    // с Path ≠ /. Иначе сервис пользователя на {id}.preview.conexyai.ru (его Set-Cookie проксируется как есть)
    // мог поставить conexy_auth с Domain=conexyai.ru и подсунуть жертве свою сессию (fixation).
    private const string AuthCookieName = "__Host-conexy_auth";
    // Старое имя больше НЕ читается (его как раз и подделывают), только стирается при выходе/сбросе.
    private const string LegacyAuthCookieName = "conexy_auth";

    // MOBILE_HANDOFF: вход из Android-приложения. Google запрещает OAuth внутри WebView, поэтому
    // логин идёт в системном браузере, а сессия возвращается приложению одноразовым кодом:
    // браузер → conexy.app://auth?code=… → приложение меняет код на JWT.
    private const string MobileRedirectUri = "conexy.app://auth";
    private const string MobileRedirectCookie = "conexy_mobile_redirect";
    // SEC_MOBILE_PKCE: code_challenge переживает круг через GitHub в этой cookie.
    private const string MobileChallengeCookie = "conexy_mobile_challenge";
    private const string MobileOutdatedMessage =
        "Эта версия приложения ConexyAI устарела. Обновите приложение до последней версии и войдите снова.";
    private static readonly TimeSpan MobileHandoffTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (TokenResponse Token, string Challenge, DateTime ExpiresUtc)> MobileHandoffs = new();

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

    // MOBILE_HANDOFF: точка входа для приложения.
    // SEC_MOBILE_PKCE: приложение обязано прислать code_challenge (S256). Код из deep link без verifier
    // бесполезен, поэтому чужое приложение, перехватившее conexy.app://, или сайт, открывший этот URL
    // в браузере жертвы, сессию не получат. Уже залогиненному пользователю код больше не
    // выдаётся молча: сначала страница подтверждения, сам код — только по POST с неё.
    /// <summary>Starts the login flow for the Android app and returns the session via a one-time code.</summary>
    [HttpGet("mobile")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public async Task<IActionResult> MobileLogin(
        [FromQuery] string? redirect,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
        CancellationToken ct)
    {
        // MOBILE_REDIRECT: только наш собственный scheme — иначе это открытый редирект с токеном.
        if (!string.Equals(redirect, MobileRedirectUri, StringComparison.Ordinal))
            return BadRequest(new { error = "unsupported_redirect" });

        // Старые APK (1.0.x) challenge не шлют — они получают понятную просьбу обновиться.
        if (!IsValidCodeChallenge(codeChallenge)
            || (codeChallengeMethod is not null && !string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal)))
            return MobileMessagePage(StatusCodes.Status400BadRequest, MobileOutdatedMessage);

        if (Request.Cookies[AuthCookieName] is { Length: > 0 } cookie
            && await ValidateCookieTokenAsync(cookie, ct) is { } session)
        {
            return await MobileConfirmPageAsync(session, codeChallenge!, ct);
        }

        var shortLived = new CookieOptions
        {
            HttpOnly = true,
            Secure = !_environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(10)
        };
        Response.Cookies.Append(MobileRedirectCookie, MobileRedirectUri, shortLived);
        Response.Cookies.Append(MobileChallengeCookie, codeChallenge!, shortLived);

        return StartGitHubLogin();
    }

    // SEC_MOBILE_PKCE: подтверждение «Войти в приложение как …». Cookie сессии SameSite=Lax на кросс-сайтовый
    // POST не уходит, а Origin/Sec-Fetch-Site дополнительно отсекают чужие формы.
    /// <summary>Issues the one-time code after the signed-in user confirmed the app login.</summary>
    [HttpPost("mobile/confirm")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public async Task<IActionResult> MobileConfirm(
        [FromForm(Name = "code_challenge")] string? codeChallenge, CancellationToken ct)
    {
        if (!IsSameOriginRequest())
            return MobileMessagePage(StatusCodes.Status403Forbidden, "Запрос отклонён. Вернитесь в приложение и нажмите «Войти» ещё раз.");

        if (!IsValidCodeChallenge(codeChallenge))
            return MobileMessagePage(StatusCodes.Status400BadRequest, MobileOutdatedMessage);

        if (Request.Cookies[AuthCookieName] is not { Length: > 0 } cookie
            || await ValidateCookieTokenAsync(cookie, ct) is not { } session)
            return MobileMessagePage(StatusCodes.Status401Unauthorized, "Сессия на сайте не найдена. Вернитесь в приложение и нажмите «Войти» ещё раз.");

        _logger.LogInformation("Mobile login: the user {UserId} confirmed reusing the browser session.", session.Principal.FindFirst("sub")?.Value);
        return MobileReturn(session.Response, codeChallenge!);
    }

    // MOBILE_HANDOFF: обмен одноразового кода на сессию. Код живёт две минуты и удаляется при
    // первом использовании, поэтому перехват deep link не даёт долговременного доступа.
    // SEC_MOBILE_PKCE: и без code_verifier, который знает только начавшее вход приложение, код ничего не стоит.
    // Код сжигается и при неверном verifier — перебирать нечего.
    /// <summary>Exchanges the one-time handoff code from the app link (plus the PKCE verifier) for a JWT.</summary>
    [HttpPost("handoff")]
    [EnableRateLimiting(AuthRateLimitPolicies.GitHubOAuth)]
    public IActionResult ExchangeHandoff([FromBody] MobileHandoffRequest? request)
    {
        if (request?.Code is not { Length: > 0 } code || !MobileHandoffs.TryRemove(code, out var entry))
            return BadRequest(new { error = "code_expired" });

        if (entry.ExpiresUtc < DateTime.UtcNow)
            return BadRequest(new { error = "code_expired" });

        if (!VerifyPkce(request.CodeVerifier, entry.Challenge))
            return BadRequest(new { error = "invalid_code_verifier", message = MobileOutdatedMessage });

        return Ok(entry.Token);
    }

    /// <summary>RFC 7636 S256 challenge: base64url (no padding) of a SHA-256 digest = 43 characters.</summary>
    public static bool IsValidCodeChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(IsBase64UrlChar);

    /// <summary>Checks <c>BASE64URL(SHA256(ASCII(verifier))) == challenge</c> in constant time.</summary>
    public static bool VerifyPkce(string? verifier, string challenge)
    {
        // RFC 7636 §4.1: 43..128 символов из [A-Z a-z 0-9 - . _ ~].
        if (verifier is not { Length: >= 43 and <= 128 } || !verifier.All(c => IsBase64UrlChar(c) || c is '.' or '~'))
            return false;

        var computed = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(computed),
            System.Text.Encoding.ASCII.GetBytes(challenge));
    }

    private static bool IsBase64UrlChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // SEC_MOBILE_PKCE: форма подтверждения должна прийти с нашего же origin (браузеры шлют Origin на POST).
    private bool IsSameOriginRequest()
    {
        var fetchSite = Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite != "same-origin")
            return false;

        var origin = Request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && string.Equals(uri.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IActionResult> MobileConfirmPageAsync(ValidatedToken session, string codeChallenge, CancellationToken ct)
    {
        string account = "ваш аккаунт";
        if (session.Principal.TryGetUserId(out var userId)
            && await _userRepository.GetByIdAsync(userId, ct) is { } user)
        {
            account = user.Email ?? user.GitHubUsername ?? account;
        }

        var encodedAccount = System.Net.WebUtility.HtmlEncode(account);
        var encodedChallenge = System.Net.WebUtility.HtmlEncode(codeChallenge);
        return MobileHtml(StatusCodes.Status200OK,
            $"<p style=\"margin:0;text-align:center;padding:0 24px\">Войти в приложение ConexyAI как <b>{encodedAccount}</b>?</p>" +
            "<form method=\"post\" action=\"/api/auth/mobile/confirm\" style=\"margin:0\">" +
            $"<input type=\"hidden\" name=\"code_challenge\" value=\"{encodedChallenge}\">" +
            "<button type=\"submit\" style=\"border:0;border-radius:10px;padding:12px 22px;background:#0e7490;" +
            "color:#fff;font:inherit;font-weight:600;cursor:pointer\">Войти</button></form>" +
            "<a style=\"color:#9aa4b2\" href=\"/\">Отмена</a>");
    }

    private ContentResult MobileMessagePage(int statusCode, string message) =>
        MobileHtml(statusCode, $"<p style=\"margin:0;text-align:center;padding:0 24px\">{System.Net.WebUtility.HtmlEncode(message)}</p>");

    private ContentResult MobileHtml(int statusCode, string body)
    {
        Response.Headers.CacheControl = "no-store";
        // Страницу подтверждения нельзя встраивать во фрейм (clickjacking кнопки «Войти»).
        Response.Headers.XFrameOptions = "DENY";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Content =
                "<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title>ConexyAI</title></head>" +
                "<body style=\"margin:0;height:100vh;display:flex;flex-direction:column;gap:16px;align-items:center;" +
                "justify-content:center;background:#0a0a0a;color:#f2f4f8;font:16px/1.5 -apple-system,Segoe UI,Roboto,sans-serif\">" +
                body +
                "</body></html>",
        };
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
                var challenge = Request.Cookies[MobileChallengeCookie];
                Response.Cookies.Delete(MobileRedirectCookie);
                Response.Cookies.Delete(MobileChallengeCookie);
                if (!IsValidCodeChallenge(challenge))
                    return MobileMessagePage(StatusCodes.Status400BadRequest, MobileOutdatedMessage);
                _logger.LogInformation("GitHub callback success: handing the session back to the app.");
                return MobileReturn(token, challenge!);
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
            DeleteAuthCookies();
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

        DeleteAuthCookies();
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
        // SEC_COOKIE_PREFIX: __Host- требует Secure + Path=/ + без Domain. Secure ставим всегда, не
        // глядя на Request.IsHttps: за nginx/Cloudflare схема запроса к бэкенду — http. Chrome/Firefox
        // принимают Secure-cookie и на http://localhost, так что локальная разработка не ломается.
        Response.Cookies.Append(AuthCookieName, token.Token, AuthCookieOptions(token.ExpiresAtUtc));
        Response.Cookies.Delete(LegacyAuthCookieName);
    }

    private static CookieOptions AuthCookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        Path = "/",
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Expires = expires
    };

    private void DeleteAuthCookies()
    {
        // Удаление __Host- cookie браузер примет только с теми же Secure и Path=/.
        Response.Cookies.Delete(AuthCookieName, AuthCookieOptions(null));
        Response.Cookies.Delete(LegacyAuthCookieName);
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
    private IActionResult MobileReturn(TokenResponse token, string codeChallenge)
    {
        // SEC_MOBILE_PKCE: 128 бит из CSPRNG вместо Guid; код связан с challenge приложения.
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        MobileHandoffs[code] = (token, codeChallenge, DateTime.UtcNow.Add(MobileHandoffTtl));

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
// SEC_MOBILE_PKCE: + code_verifier, из которого был посчитан code_challenge.
public record MobileHandoffRequest(string? Code, string? CodeVerifier = null);

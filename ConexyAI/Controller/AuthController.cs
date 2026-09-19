using System.Security.Cryptography;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    private readonly ITokenService _tokenService;
    private readonly IWebHostEnvironment _environment;
    // GITHUB_OAUTH: добавлено 2026-09-19
    private readonly IGitHubOAuthService _gitHubOAuthService;
    private readonly GitHubOAuthOptions _gitHubOAuthOptions;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<AuthController> _logger;
    // EMAIL_AUTH: добавлено 2026-09-19
    private readonly IEmailAuthService _emailAuthService;

    public AuthController(
        ITokenService tokenService,
        IWebHostEnvironment environment,
        IGitHubOAuthService gitHubOAuthService,
        IOptions<GitHubOAuthOptions> gitHubOAuthOptions,
        IUserRepository userRepository,
        ILogger<AuthController> logger,
        IEmailAuthService emailAuthService)
    {
        _tokenService = tokenService;
        _environment = environment;
        _gitHubOAuthService = gitHubOAuthService;
        _gitHubOAuthOptions = gitHubOAuthOptions.Value;
        _userRepository = userRepository;
        _logger = logger;
        _emailAuthService = emailAuthService;
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

        return Ok(_tokenService.CreateToken(effectiveUserId));
    }

    // GITHUB_OAUTH: добавлено 2026-09-19
    /// <summary>Starts the GitHub OAuth flow: sets a CSRF <c>state</c> cookie and redirects to GitHub.</summary>
    [HttpGet("github/login")]
    public IActionResult GitHubLogin()
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
            var token = _tokenService.CreateToken(result.UserId, result.IsAdmin);

            // GITHUB_OAUTH: добавлено 2026-09-19 — deliver the JWT via an httpOnly cookie
            // (not a URL fragment), so a repeated callback can no longer clobber it. The SPA
            // reads it back through GET /api/auth/session and stores it in localStorage.
            SetAuthCookie(token);

            var redirectUrl = $"{frontendBase}/";
            _logger.LogInformation("GitHub callback success: set httpOnly cookie and redirecting to '{RedirectUrl}'.", redirectUrl);
            return Redirect(redirectUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitHub callback failed for code prefix '{CodePrefix}'.", CodePrefix(code));
            return RedirectToFrontendError(frontendBase, ex.Message);
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
    public ActionResult<TokenResponse> GetSession()
    {
        var token = Request.Cookies[AuthCookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized();
        }

        var validated = _tokenService.ValidateToken(token);
        if (validated is null)
        {
            // Expired/invalid: drop the stale cookie so the client cleanly re-authenticates.
            Response.Cookies.Delete(AuthCookieName);
            return Unauthorized();
        }

        return Ok(validated);
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Creates a new email/password account and logs the user in immediately.</summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] EmailPasswordRequest request, CancellationToken ct)
    {
        try
        {
            var user = await _emailAuthService.RegisterAsync(request.Email, request.Password, ct);
            return Ok(IssueSession(user));
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Logs in with an email/password account.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] EmailPasswordRequest request, CancellationToken ct)
    {
        try
        {
            var user = await _emailAuthService.LoginAsync(request.Email, request.Password, ct);
            return Ok(IssueSession(user));
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Clears the session cookie (logout).</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
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
            user.SubscriptionTier.ToString(),
            user.IsAdmin));
    }

    // EMAIL_AUTH: добавлено 2026-09-19
    private TokenResponse IssueSession(User user)
    {
        var token = _tokenService.CreateToken(user.Id, user.IsAdmin);
        SetAuthCookie(token);
        return token;
    }

    private void SetAuthCookie(TokenResponse token)
    {
        // EMAIL_AUTH: добавлено 2026-09-19 — Expires is set explicitly so the browser treats
        // the cookie as persistent (not a session cookie). It lives exactly as long as the JWT
        // (30 days), enabling "remember me" across browser restarts.
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

    private RedirectResult RedirectToFrontendError(string frontendBase, string message)
    {
        return Redirect($"{frontendBase}/?auth=error&message={Uri.EscapeDataString(message)}");
    }
}

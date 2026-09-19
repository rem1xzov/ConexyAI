using System.Security.Cryptography;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConexyAI.Controller;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private const string StateCookieName = "github_oauth_state";

    private readonly ITokenService _tokenService;
    private readonly IWebHostEnvironment _environment;
    // GITHUB_OAUTH: добавлено 2026-09-19
    private readonly IGitHubOAuthService _gitHubOAuthService;
    private readonly GitHubOAuthOptions _gitHubOAuthOptions;
    private readonly IUserRepository _userRepository;

    public AuthController(
        ITokenService tokenService,
        IWebHostEnvironment environment,
        IGitHubOAuthService gitHubOAuthService,
        IOptions<GitHubOAuthOptions> gitHubOAuthOptions,
        IUserRepository userRepository)
    {
        _tokenService = tokenService;
        _environment = environment;
        _gitHubOAuthService = gitHubOAuthService;
        _gitHubOAuthOptions = gitHubOAuthOptions.Value;
        _userRepository = userRepository;
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
    /// user, issues a real JWT and redirects the SPA with the token in the URL fragment.
    /// </summary>
    [HttpGet("github/callback")]
    public async Task<IActionResult> GitHubCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken ct)
    {
        var frontendBase = FrontendBaseUrl();

        if (string.IsNullOrWhiteSpace(code))
        {
            return RedirectToFrontendError(frontendBase, "missing_code");
        }

        if (!ValidateState(state))
        {
            return RedirectToFrontendError(frontendBase, "state_mismatch");
        }

        try
        {
            var result = await _gitHubOAuthService.HandleCallbackAsync(code, ct);
            var token = _tokenService.CreateToken(result.UserId);

            // Deliver via the URL fragment (never sent to the server / access logs, and not
            // leaked through Referer). The SPA reads it, stores it in localStorage (same place
            // the dev-token used) and strips the fragment from the address bar.
            return Redirect($"{frontendBase}/#token={Uri.EscapeDataString(token.Token)}&expiresAtUtc={Uri.EscapeDataString(token.ExpiresAtUtc.ToString("o"))}");
        }
        catch (Exception ex)
        {
            return RedirectToFrontendError(frontendBase, ex.Message);
        }
    }

    private static string GenerateState()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

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
        return Redirect($"{frontendBase}/#auth=error&message={Uri.EscapeDataString(message)}");
    }
}

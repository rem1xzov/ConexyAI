using ConexyAI.Contract;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly ITokenService _tokenService;
    private readonly IWebHostEnvironment _environment;

    public AuthController(ITokenService tokenService, IWebHostEnvironment environment)
    {
        _tokenService = tokenService;
        _environment = environment;
    }

    /// <summary>
    /// Issues a signed JWT for local development/testing only. In production the
    /// token must come from the real identity provider, so this endpoint is
    /// hidden (404) outside the Development environment.
    /// </summary>
    [HttpPost("dev-token")]
    public ActionResult<TokenResponse> CreateDevToken([FromQuery] Guid? userId = null)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var effectiveUserId = userId ?? Guid.NewGuid();
        return Ok(_tokenService.CreateToken(effectiveUserId));
    }
}

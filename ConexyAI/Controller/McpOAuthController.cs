using System.Net;
using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using ConexyAI.Service.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// MCP_OAUTH: добавлено 2026-11-03 — авторизация личного MCP-сервера в браузере.
//
// Почему отдельный контроллер: callback открывает браузер пользователя, и он анонимный —
// секретом в нём является одноразовый state, а не JWT. Start/status, наоборот, требуют сессию:
// пользователь должен подтвердить адрес сервера тем же SSRF-гардом, что и при сохранении.
[ApiController]
[Route("api/mcp/oauth")]
[Authorize]
public class McpOAuthController : ControllerBase
{
    private readonly IMcpOAuthService _oauth;
    private readonly IUserIntegrationsService _integrations;
    private readonly PublicUrlGuard _urlGuard;
    private readonly ILogger<McpOAuthController> _logger;

    public McpOAuthController(
        IMcpOAuthService oauth,
        IUserIntegrationsService integrations,
        PublicUrlGuard urlGuard,
        ILogger<McpOAuthController> logger)
    {
        _oauth = oauth;
        _integrations = integrations;
        _urlGuard = urlGuard;
        _logger = logger;
    }

    /// <summary>Готовит ссылку авторизации у сервера авторизации MCP-сервера.</summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] McpOAuthStartRequest? request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        if (request is null || string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { error = "MCP_URL_REQUIRED", message = "Укажите URL MCP-сервера." });

        // SEC_MCP_SSRF: тот же guard, что при сохранении сервера, — иначе start стал бы SSRF-прокси.
        var urlError = await McpRegistryFactory.ValidateUserUrlAsync(_urlGuard, request.Url, ct);
        if (urlError is not null)
            return BadRequest(new { error = "MCP_URL_REJECTED", message = urlError });

        var serverId = request.ServerId?.Trim() ?? string.Empty;
        var servers = await _integrations.GetMcpServersAsync(userId, ct);
        var stored = servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        var serverName = string.IsNullOrWhiteSpace(stored?.Name) ? new Uri(request.Url.Trim()).Host : stored!.Name!.Trim();

        var result = await _oauth.StartAsync(
            userId, serverId, serverName, request.Url.Trim(), request.ResourceMetadataUrl, ct);

        if (string.IsNullOrWhiteSpace(result.AuthorizeUrl))
        {
            _logger.LogWarning("MCP OAuth start failed: {Code}", result.Error);
            return BadRequest(new
            {
                error = result.Error ?? "MCP_OAUTH_FAILED",
                message = result.Message ?? "Не удалось начать авторизацию MCP-сервера.",
            });
        }

        return Ok(new McpOAuthStartResponse(result.AuthorizeUrl));
    }

    /// <summary>
    /// Callback сервера авторизации. Анонимный: state одноразовый и подписан случайным секретом;
    /// страница не отражает пользовательские данные, кроме названия сервера (экранируется).
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        var result = await _oauth.CompleteAsync(code, state, ct);
        if (!result.Success)
        {
            return HtmlPage(
                StatusCodes.Status400BadRequest,
                "Не удалось подключить MCP-сервер",
                result.Error ?? "Повторите авторизацию из настроек ConexyAI.");
        }

        return HtmlPage(
            StatusCodes.Status200OK,
            "Готово",
            $"ConexyAI подключён к {result.ServerName}. Можно закрыть вкладку.");
    }

    /// <summary>Есть ли у пользователя действующая авторизация сервера и до какого срока.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? serverId, [FromQuery] string? url, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });

        var target = url?.Trim();
        if (string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(serverId))
        {
            var servers = await _integrations.GetMcpServersAsync(userId, ct);
            target = servers
                .FirstOrDefault(s => string.Equals(s.Id, serverId.Trim(), StringComparison.OrdinalIgnoreCase))?.Url;
        }

        if (string.IsNullOrWhiteSpace(target))
            return Ok(new McpOAuthStatusDto(false, null));

        var status = await _oauth.GetStatusAsync(userId, target, ct);
        return Ok(new McpOAuthStatusDto(status.Authorized, status.ExpiresAt));
    }

    // SEC_OAUTH_HTML: страница без скриптов и внешних ресурсов; CSP default-src 'none' не даёт
    // выполнить ничего лишнего, даже если в названии сервера окажется разметка.
    private IActionResult HtmlPage(int statusCode, string title, string text)
    {
        var html =
            "<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>" + WebUtility.HtmlEncode(title) + "</title></head><body>" +
            "<h1>" + WebUtility.HtmlEncode(title) + "</h1>" +
            "<p>" + WebUtility.HtmlEncode(text) + "</p>" +
            "</body></html>";

        Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        Response.Headers["Cache-Control"] = "no-store";
        Response.StatusCode = statusCode;
        return Content(html, "text/html; charset=utf-8", System.Text.Encoding.UTF8);
    }
}

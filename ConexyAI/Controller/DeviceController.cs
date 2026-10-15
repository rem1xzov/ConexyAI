using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service.Device;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ConexyAI.Controller;

// DESKTOP_AGENT: добавлено 2026-10-15 — ключи десктопного клиента.
public sealed record CreateDeviceKeyRequest(string? Name);

public sealed record ExchangeDeviceKeyRequest(string Key);

[ApiController]
[Route("api/device")]
public class DeviceController : ControllerBase
{
    private readonly IDeviceKeyService _keys;
    private readonly ILogger<DeviceController> _logger;

    public DeviceController(IDeviceKeyService keys, ILogger<DeviceController> logger)
    {
        _keys = keys;
        _logger = logger;
    }

    /// <summary>Выпускает ключ для десктопа. Открытый ключ в ответе показывается один раз.</summary>
    [HttpPost("keys")]
    [Authorize]
    public async Task<IActionResult> Issue([FromBody] CreateDeviceKeyRequest? request, CancellationToken ct)
    {
        try
        {
            var issued = await _keys.IssueAsync(User.GetUserId(), request?.Name, ct);
            _logger.LogInformation("Device key issued: user={UserId} key={KeyId}", User.GetUserId(), issued.Info.Id);
            return Ok(new { key = issued.Key, info = issued.Info });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "DEVICE_KEY_LIMIT", message = ex.Message });
        }
    }

    [HttpGet("keys")]
    [Authorize]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _keys.ListAsync(User.GetUserId(), ct));

    [HttpDelete("keys/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (!await _keys.RevokeAsync(User.GetUserId(), id, ct))
            return NotFound();

        _logger.LogInformation("Device key revoked: user={UserId} key={KeyId}", User.GetUserId(), id);
        return NoContent();
    }

    /// <summary>
    /// Десктоп меняет свой ключ на обычный JWT и дальше работает через тот же SignalR-хаб и API.
    /// Ответ одинаков для неверного и отозванного ключа — перебор ничего не раскрывает.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.DeviceToken)]
    public async Task<ActionResult<TokenResponse>> Exchange([FromBody] ExchangeDeviceKeyRequest request, CancellationToken ct)
    {
        var token = await _keys.ExchangeAsync(request.Key, ct);
        return token is null
            ? Unauthorized(new { error = "INVALID_DEVICE_KEY", message = "Ключ недействителен или отозван." })
            : Ok(token);
    }
}

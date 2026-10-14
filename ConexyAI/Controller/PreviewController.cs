using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// PREVIEW: вкладка «Превью» — адрес (поддомен), токен и статус сервиса агента для чата.
[ApiController]
[Route("api/sessions/{sessionId:guid}/preview")]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly IPreviewService _preview;
    private readonly IChatAccessService _chatAccess;

    public PreviewController(IPreviewService preview, IChatAccessService chatAccess)
    {
        _preview = preview;
        _chatAccess = chatAccess;
    }

    public record PreviewDto(bool Running, string? Url, string? OpenUrl, string? Token, string? Subdomain, int? Port, string? Command, DateTime? StartedAt);

    [HttpGet]
    public async Task<ActionResult<PreviewDto>> Get(Guid sessionId, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbid();

        var info = _preview.GetForChat(sessionId);
        if (info is null)
            return Ok(new PreviewDto(false, null, null, null, null, null, null, null));

        var url = _preview.PublicUrl(info);
        return Ok(new PreviewDto(true, url, $"{url}?token={info.Token}", info.Token, info.Id, info.Port, info.Command, info.StartedAt));
    }

    [HttpGet("logs")]
    public async Task<IActionResult> Logs(Guid sessionId, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbid();

        return Ok(new { logs = await _preview.LogsAsync(sessionId, 200, ct) });
    }

    [HttpDelete]
    public async Task<IActionResult> Stop(Guid sessionId, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized(new { error = "Valid user id claim not found in token." });
        if (!await _chatAccess.CanReadAsync(userId, sessionId, ct))
            return Forbid();

        await _preview.StopAsync(sessionId);
        return NoContent();
    }
}

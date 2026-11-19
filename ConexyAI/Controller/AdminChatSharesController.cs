using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// SHARE_TO_ADMIN: добавлено 2026-11-19 — админ видит чаты, которыми с ним поделились, вместе с
// действиями агента и скриншотом визуального бага.
[ApiController]
[Route("api/admin/chats/shared")]
[Authorize]
public class AdminChatSharesController : ControllerBase
{
    private readonly IChatShareService _shares;
    private readonly ILogger<AdminChatSharesController> _logger;

    public AdminChatSharesController(IChatShareService shares, ILogger<AdminChatSharesController> logger)
    {
        _shares = shares;
        _logger = logger;
    }

    /// <summary>Inbox of chats users shared with the admins (admin only).</summary>
    [HttpGet]
    public async Task<ActionResult<AdminSharedChatsResponse>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        if (!User.IsAdmin())
            return Forbid();

        return Ok(await _shares.ListAsync(page, pageSize, status, search, ct));
    }

    /// <summary>Full view of one shared chat: transcript, agent actions, screenshot (admin only).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AdminSharedChatDetail>> Get(Guid id, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        var detail = await _shares.GetAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>Closes a shared-chat report with an optional note (admin only).</summary>
    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, [FromBody] AdminCloseSharedChatRequest? request, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        if (!await _shares.CloseAsync(id, request?.Note, ct))
            return NotFound();

        _logger.LogInformation("Admin {AdminId} closed shared chat report {ReportId}.", User.GetUserId(), id);
        return Ok(new { success = true });
    }
}

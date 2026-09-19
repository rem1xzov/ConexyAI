using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// SUPPORT: добавлено 2026-09-19
[ApiController]
[Route("api/admin/support")]
[Authorize]
public class AdminSupportController : ControllerBase
{
    private readonly ISupportService _supportService;

    public AdminSupportController(ISupportService supportService)
    {
        _supportService = supportService;
    }

    /// <summary>Lists all support tickets (optionally filtered by status and searched by user).</summary>
    [HttpGet("tickets")]
    public async Task<ActionResult<IReadOnlyList<AdminSupportTicketDto>>> GetTickets(
        [FromQuery] string? status, [FromQuery] string? search, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        return Ok(await _supportService.GetAdminTicketsAsync(status, search, ct));
    }

    /// <summary>Returns a single ticket with full message history.</summary>
    [HttpGet("tickets/{id:guid}")]
    public async Task<ActionResult<SupportTicketDto>> GetTicket(Guid id, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        var ticket = await _supportService.GetAdminTicketAsync(id, ct);
        if (ticket is null)
            return NotFound();

        return Ok(ticket);
    }

    /// <summary>Closes a support ticket.</summary>
    [HttpPost("tickets/{id:guid}/close")]
    public async Task<IActionResult> CloseTicket(Guid id, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();

        await _supportService.CloseTicketAsync(id, ct);
        return Ok(new { success = true });
    }
}

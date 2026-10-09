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

    /// <summary>
    /// Posts an operator reply to a ticket. Always stored as an admin message and mutes the bot
    /// until the user explicitly returns to it (rule 8). Kept separate from the user endpoint so
    /// the two roles can never be confused.
    /// </summary>
    [HttpPost("tickets/{id:guid}/messages")]
    public async Task<ActionResult<SupportMessageDto>> AddMessage(
        Guid id, [FromBody] SupportMessageRequest request, CancellationToken ct)
    {
        if (!User.IsAdmin())
            return Forbid();
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            var message = await _supportService.AddMessageAsync(id, userId, request.Content, isFromAdmin: true, ct);
            return Ok(message);
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
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

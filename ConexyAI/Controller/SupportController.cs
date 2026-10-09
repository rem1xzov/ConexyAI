using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// SUPPORT: добавлено 2026-09-19
[ApiController]
[Route("api/support")]
[Authorize]
public class SupportController : ControllerBase
{
    private readonly ISupportService _supportService;

    public SupportController(ISupportService supportService)
    {
        _supportService = supportService;
    }

    /// <summary>Creates a support ticket for the current user (or reuses their open one).</summary>
    [HttpPost("tickets")]
    public async Task<ActionResult<SupportTicketDto>> CreateOrGetTicket(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        return Ok(await _supportService.GetOrCreateTicketAsync(userId, ct));
    }

    /// <summary>Returns the current user's open ticket with full message history.</summary>
    [HttpGet("tickets/mine")]
    public async Task<ActionResult<SupportTicketDto>> GetMyTicket(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        var ticket = await _supportService.GetMyTicketAsync(userId, ct);
        if (ticket is null)
            return NotFound();

        return Ok(ticket);
    }

    /// <summary>
    /// Adds a message to a ticket. Regular users may only write in their own ticket; admins
    /// (per the JWT <c>isAdmin</c> claim) may write in any ticket as an admin.
    /// </summary>
    [HttpPost("tickets/{id:guid}/messages")]
    public async Task<ActionResult<SupportMessageDto>> AddMessage(
        Guid id, [FromBody] SupportMessageRequest request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            var message = await _supportService.AddMessageAsync(id, userId, request.Content, User.IsAdmin(), ct);
            return Ok(message);
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }

    // SUPPORT_BOT: «Не помогло» — эскалация на оператора.
    [HttpPost("tickets/{id:guid}/escalate")]
    public Task<ActionResult<SupportTicketDto>> Escalate(Guid id, CancellationToken ct) =>
        Transition(id, (userId, token) => _supportService.EscalateAsync(id, userId, token), ct);

    // SUPPORT_BOT: «Вернуться к боту» — бот снова отвечает, оператор остаётся в чате.
    [HttpPost("tickets/{id:guid}/return-to-bot")]
    public Task<ActionResult<SupportTicketDto>> ReturnToBot(Guid id, CancellationToken ct) =>
        Transition(id, (userId, token) => _supportService.ReturnToBotAsync(id, userId, token), ct);

    // SUPPORT_BOT: «Отменить обращение» — тикет закрывается.
    [HttpPost("tickets/{id:guid}/cancel")]
    public Task<ActionResult<SupportTicketDto>> Cancel(Guid id, CancellationToken ct) =>
        Transition(id, (userId, token) => _supportService.CancelAsync(id, userId, token), ct);

    private async Task<ActionResult<SupportTicketDto>> Transition(
        Guid id, Func<Guid, CancellationToken, Task<SupportTicketDto>> action, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        try
        {
            return Ok(await action(userId, ct));
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }
}

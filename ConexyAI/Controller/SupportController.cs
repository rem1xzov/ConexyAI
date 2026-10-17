using ConexyAI.Contract;
using ConexyAI.Extensions;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// SUPPORT: добавлено 2026-09-19
// SUPPORT_MULTI: изменено 2026-10-18 — несколько обращений на пользователя, как чаты в мессенджере,
// и вложения (картинки, файлы) в сообщениях.
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

    /// <summary>Все обращения пользователя для списка: тема, статус, последнее сообщение, непрочитанные.</summary>
    [HttpGet("tickets")]
    public async Task<ActionResult<IReadOnlyList<SupportTicketSummaryDto>>> ListTickets(CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        return Ok(await _supportService.ListMyTicketsAsync(userId, ct));
    }

    /// <summary>
    /// Создаёт НОВОЕ обращение. Без тела запроса (старые клиенты) ведёт себя как раньше: возвращает
    /// открытое обращение, если оно есть, иначе создаёт.
    /// </summary>
    [HttpPost("tickets")]
    public async Task<ActionResult<SupportTicketDto>> CreateTicket(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CreateSupportTicketRequest? request,
        CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        if (request is null)
            return Ok(await _supportService.GetOrCreateTicketAsync(userId, ct));
        return await Handle(() => _supportService.CreateTicketAsync(userId, request.Subject, ct));
    }

    /// <summary>Returns the current user's latest open ticket with full message history.</summary>
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

    /// <summary>Одно обращение пользователя целиком; отмечает его прочитанным.</summary>
    [HttpGet("tickets/{id:guid}")]
    public async Task<ActionResult<SupportTicketDto>> GetTicket(Guid id, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        return await Handle(() => _supportService.GetMyTicketByIdAsync(id, userId, ct));
    }

    /// <summary>
    /// Adds a user message to the caller's own ticket. This endpoint is always the *user* side of
    /// the conversation, even when the caller happens to be an admin: an admin testing support from
    /// their own account must not be mistaken for an operator, or the bot would go silent (rule 8).
    /// Operator replies go through the admin endpoint instead.
    /// </summary>
    [HttpPost("tickets/{id:guid}/messages")]
    public async Task<ActionResult<SupportMessageDto>> AddMessage(
        Guid id, [FromBody] SupportMessageRequest request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();

        // SUPPORT_ROLES: isFromAdmin всегда false — админ отвечает через AdminSupportController.
        return await Handle(() => _supportService.AddMessageAsync(id, userId, request.Content, isFromAdmin: false, ct));
    }

    // SUPPORT_ATTACHMENTS: сообщение с картинками/файлами (multipart/form-data: content + files).
    [HttpPost("tickets/{id:guid}/messages/upload")]
    [RequestSizeLimit(SupportUploads.MaxRequestBytes)]
    public async Task<ActionResult<SupportMessageDto>> AddMessageWithFiles(
        Guid id, [FromForm] string? content, [FromForm] List<IFormFile>? files, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        return await Handle(async () => await _supportService.AddMessageAsync(
            id, userId, content ?? string.Empty, isFromAdmin: false, await SupportUploads.ReadAsync(files, ct), ct));
    }

    /// <summary>Байты вложения — только владельцу обращения (и операторам).</summary>
    [HttpGet("attachments/{id:guid}")]
    public async Task<IActionResult> GetAttachment(Guid id, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        var attachment = await _supportService.GetAttachmentAsync(id, userId, User.IsAdmin(), ct);
        return attachment is null ? NotFound() : SupportUploads.File(this, attachment);
    }

    // SUPPORT_BOT: «Не помогло» — эскалация на оператора.
    [HttpPost("tickets/{id:guid}/escalate")]
    public Task<ActionResult<SupportTicketDto>> Escalate(Guid id, CancellationToken ct) =>
        Transition(id, (userId, token) => _supportService.EscalateAsync(id, userId, token), ct);

    // SUPPORT_BOT: «Вернуться к боту» — бот снова отвечает, оператор остаётся в чате.
    [HttpPost("tickets/{id:guid}/return-to-bot")]
    public Task<ActionResult<SupportTicketDto>> ReturnToBot(Guid id, CancellationToken ct) =>
        Transition(id, (userId, token) => _supportService.ReturnToBotAsync(id, userId, token), ct);

    // SUPPORT_BOT: «Закрыть обращение» — тикет закрывается.
    [HttpPost("tickets/{id:guid}/cancel")]
    public Task<ActionResult<SupportTicketDto>> Cancel(Guid id, CancellationToken ct) =>
        Transition(id, (userId, token) => _supportService.CancelAsync(id, userId, token), ct);

    private Task<ActionResult<SupportTicketDto>> Transition(
        Guid id, Func<Guid, CancellationToken, Task<SupportTicketDto>> action, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Task.FromResult<ActionResult<SupportTicketDto>>(Unauthorized());
        return Handle(() => action(userId, ct));
    }

    private async Task<ActionResult<T>> Handle<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (AuthException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
        }
    }
}

// SUPPORT_ATTACHMENTS: общие куски для пользовательского и админского контроллеров.
internal static class SupportUploads
{
    public const long MaxRequestBytes = SupportService.MaxFilesPerMessage * SupportService.MaxFileBytes + 1_000_000;

    public static async Task<IReadOnlyList<SupportUpload>> ReadAsync(List<IFormFile>? files, CancellationToken ct)
    {
        var result = new List<SupportUpload>();
        foreach (var file in files ?? [])
        {
            if (file.Length > SupportService.MaxFileBytes)
                throw new AuthException("file_too_large", $"Файл «{file.FileName}» больше 10 МБ.", 400);
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            result.Add(new SupportUpload(file.FileName, file.ContentType, buffer.ToArray()));
        }
        return result;
    }

    /// <summary>
    /// Отдаёт вложение. Картинки — inline (показываются в чате), всё остальное — только скачиванием;
    /// nosniff и CSP sandbox не дают браузеру исполнить содержимое как страницу.
    /// </summary>
    public static IActionResult File(ControllerBase controller, Entity.SupportAttachment attachment)
    {
        var inline = SupportService.InlineImageTypes.Contains(attachment.ContentType);
        var headers = controller.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        headers["Cache-Control"] = "private, max-age=86400";
        var result = controller.File(attachment.Data, attachment.ContentType, inline ? null : attachment.FileName);
        if (inline)
            headers["Content-Disposition"] = "inline";
        return result;
    }
}

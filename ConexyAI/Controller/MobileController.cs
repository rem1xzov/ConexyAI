using ConexyAI.Extensions;
using ConexyAI.Repository;
using ConexyAI.Service.Phone;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConexyAI.Controller;

// PHONE_AGENT: добавлено 2026-10-29 — push-токены Android-приложения и решения из уведомлений.
public sealed record PushTokenRequest(string? Token, string? Platform);

public sealed record ActionDecisionRequest(bool Approved);

[ApiController]
[Route("api/mobile")]
[Authorize]
public class MobileController : ControllerBase
{
    private const int MinTokenLength = 20;
    private const int MaxTokenLength = 1_024;

    private readonly IMobilePushTokenRepository _tokens;
    private readonly IPhoneApprovalService _approvals;
    private readonly ILogger<MobileController> _logger;

    public MobileController(
        IMobilePushTokenRepository tokens,
        IPhoneApprovalService approvals,
        ILogger<MobileController> logger)
    {
        _tokens = tokens;
        _approvals = approvals;
        _logger = logger;
    }

    /// <summary>Сохраняет FCM-токен устройства. Повтор того же токена только обновляет LastSeenAt.</summary>
    [HttpPost("push-token")]
    public async Task<IActionResult> RegisterToken([FromBody] PushTokenRequest? request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        var token = request?.Token?.Trim();
        if (!IsValidToken(token))
            return BadRequest(new { code = "invalid_token", message = "Некорректный push-токен." });

        var platform = string.Equals(request!.Platform?.Trim(), "android", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(request.Platform)
            ? "android"
            : request.Platform!.Trim().ToLowerInvariant();
        if (platform.Length > 20 || platform.Any(char.IsControl))
            return BadRequest(new { code = "invalid_platform", message = "Некорректная платформа." });

        await _tokens.UpsertAsync(userId, token!, platform, ct);
        return Ok(new { ok = true });
    }

    /// <summary>Удаляет токен при выходе из аккаунта. Чужой или неизвестный токен ничего не меняет.</summary>
    [HttpDelete("push-token")]
    public async Task<IActionResult> RemoveToken([FromBody] PushTokenRequest? request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        var token = request?.Token?.Trim();
        if (!IsValidToken(token))
            return BadRequest(new { code = "invalid_token", message = "Некорректный push-токен." });

        await _tokens.RemoveAsync(userId, token!, ct);
        return NoContent();
    }

    /// <summary>
    /// Решение по подтверждению из уведомления («Разрешить»/«Отклонить»). 200 — принято; 409 — уже решено
    /// или время вышло; 404 — такого действия нет или оно чужое.
    /// </summary>
    [HttpPost("actions/{actionId}")]
    public async Task<IActionResult> Decide(string actionId, [FromBody] ActionDecisionRequest? request, CancellationToken ct)
    {
        if (!User.TryGetUserId(out var userId))
            return Unauthorized();
        if (!Guid.TryParse(actionId, out var id))
            return BadRequest(new { code = "invalid_action", message = "Некорректный идентификатор действия." });
        if (request is null)
            return BadRequest(new { code = "invalid_body", message = "Нужно поле approved." });

        var result = await _approvals.DecideAsync(userId, id, request.Approved, ct);
        _logger.LogInformation("Phone action {ActionId} decision via REST approved={Approved}: {Result}", id, request.Approved, result);
        return result switch
        {
            PhoneDecisionResult.Applied => Ok(new { ok = true, approved = request.Approved }),
            PhoneDecisionResult.AlreadyResolved => Conflict(new { code = "already_resolved", message = "Решение уже принято." }),
            _ => NotFound(new { code = "not_found", message = "Действие не найдено." }),
        };
    }

    private static bool IsValidToken(string? token) =>
        token is { Length: >= MinTokenLength and <= MaxTokenLength }
        && !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
